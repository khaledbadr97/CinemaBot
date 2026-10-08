using System;
using System.Collections.Generic;
using System.Linq;

namespace CinemaBot.Service
{
    internal sealed class CurrentMovieCatalogItem
    {
        public string Key { get; set; } = string.Empty;
        public string EnglishTitle { get; set; } = string.Empty;
        public string ArabicTitle { get; set; } = string.Empty;
    }

    internal sealed class CurrentMovieSubscription
    {
        public string MovieKey { get; set; } = string.Empty;
        public string MovieTitle { get; set; } = string.Empty;

        // Empty means all cinemas.
        public HashSet<string> CinemaSlugs { get; set; } =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        public CurrentMovieSubscription Clone()
        {
            return new CurrentMovieSubscription
            {
                MovieKey =
                    MovieKey,

                MovieTitle =
                    MovieTitle,

                CinemaSlugs =
                    new HashSet<string>(
                        CinemaSlugs,
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }

    internal sealed class CurrentMovieUserPreference
    {
        public long UserId { get; set; }
        public long PrivateChatId { get; set; }

        public List<CurrentMovieSubscription> Subscriptions { get; set; } =
            new List<CurrentMovieSubscription>();

        // One entry per movie/cinema/date that was already present when the
        // user subscribed or was already announced. This guarantees that the
        // user receives alerts only for newly opened booking dates.
        public HashSet<string> KnownBookingDateKeys { get; set; } =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        public CurrentMovieUserPreference Clone()
        {
            return new CurrentMovieUserPreference
            {
                UserId =
                    UserId,

                PrivateChatId =
                    PrivateChatId,

                Subscriptions =
                    Subscriptions
                        .Select(item =>
                            item.Clone())
                        .ToList(),

                KnownBookingDateKeys =
                    new HashSet<string>(
                        KnownBookingDateKeys,
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
