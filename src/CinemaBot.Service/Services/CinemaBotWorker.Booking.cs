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
        private async Task<List<DayScheduleChange>> ResolveChangeBookingUrlsAsync(
            List<DayScheduleChange> changes,
            CancellationToken stoppingToken)
        {
            var result =
                new List<DayScheduleChange>();

            foreach (DayScheduleChange day in
                     changes)
            {
                Task<ScheduleChange>[] resolutionTasks =
                    day.Changes
                        .Select(async change =>
                        {
                            if (string.IsNullOrWhiteSpace(
                                    change.BookingUrl))
                            {
                                return change;
                            }

                            // A detected schedule change must never use a possibly
                            // old redirect result. Fetch the current VOX final URL
                            // and overwrite the cache immediately.
                            string resolvedUrl =
                                await ResolveBookingUrlAsync(
                                    change.BookingUrl,
                                    day.Schedule.Url,
                                    stoppingToken,
                                    forceRefresh:
                                        true);

                            return change.WithBookingUrl(
                                SelectUsableBookingUrl(
                                    resolvedUrl,
                                    change.BookingUrl));
                        })
                        .ToArray();

                ScheduleChange[] resolved =
                    await Task.WhenAll(
                        resolutionTasks);

                result.Add(
                    DayScheduleChange.Changed(
                        day.Schedule,
                        resolved));
            }

            return result;
        }

        private async Task<MovieSchedule> ResolveMovieBookingUrlsAsync(
            MovieSchedule movie,
            string scheduleUrl,
            CancellationToken stoppingToken,
            bool forceRefresh = false)
        {
            Task<HallSchedule>[] hallTasks =
                movie.Halls
                    .Select(async hall =>
                    {
                        Task<ShowtimeSchedule>[] showtimeTasks =
                            hall.Showtimes
                                .Select(async showtime =>
                                {
                                    if (!showtime.IsAvailable ||
                                        string.IsNullOrWhiteSpace(
                                            showtime.BookingUrl))
                                    {
                                        return showtime;
                                    }

                                    string resolvedUrl =
                                        await ResolveBookingUrlAsync(
                                            showtime.BookingUrl,
                                            scheduleUrl,
                                            stoppingToken,
                                            forceRefresh);

                                    return new ShowtimeSchedule(
                                        showtime.Time,
                                        showtime.IsAvailable,
                                        SelectUsableBookingUrl(
                                            resolvedUrl,
                                            showtime.BookingUrl),
                                        showtime.Identifier);
                                })
                                .ToArray();

                        ShowtimeSchedule[] showtimes =
                            await Task.WhenAll(
                                showtimeTasks);

                        return new HallSchedule(
                            hall.Name,
                            showtimes);
                    })
                    .ToArray();

            HallSchedule[] halls =
                await Task.WhenAll(
                    hallTasks);

            return new MovieSchedule(
                movie.Title,
                halls);
        }

        private Task<string> ResolveBookingUrlAsync(
            string candidateUrl,
            string scheduleUrl,
            CancellationToken stoppingToken,
            bool forceRefresh = false)
        {
            candidateUrl =
                NormalizeGuestBookingUrl(
                    candidateUrl);

            if (!IsSafeVoxBookingUrl(
                    candidateUrl))
            {
                return Task.FromResult(
                    string.Empty);
            }

            // Final guest URLs are already usable. NormalizeGuestBookingUrl()
            // has also removed /processing before reaching this point.
            if (IsFinalGuestBookingUrl(
                    candidateUrl))
            {
                return Task.FromResult(
                    candidateUrl);
            }

            Task<string> resolutionTask;

            lock (_bookingUrlCacheLock)
            {
                RemoveExpiredBookingUrlCacheEntriesNoLock();

                if (!forceRefresh &&
                    _bookingUrlCache.TryGetValue(
                        candidateUrl,
                        out BookingUrlCacheEntry? cached) &&
                    cached.ExpiresUtc >
                    DateTime.UtcNow)
                {
                    string cachedUrl =
                        SelectUsableBookingUrl(
                            cached.ResolvedUrl,
                            candidateUrl);

                    if (!string.IsNullOrWhiteSpace(
                            cachedUrl))
                    {
                        return Task.FromResult(
                            cachedUrl);
                    }

                    _bookingUrlCache.Remove(
                        candidateUrl);
                }

                // Coalesce simultaneous requests for the same VOX link.
                // This is important when many subscribers receive the same
                // new-booking-date notification at once.
                if (_bookingUrlResolveTasks.TryGetValue(
                        candidateUrl,
                        out Task<string>? existingTask))
                {
                    return existingTask;
                }

                resolutionTask =
                    ResolveBookingUrlCoreAsync(
                        candidateUrl,
                        scheduleUrl,
                        stoppingToken);

                _bookingUrlResolveTasks[candidateUrl] =
                    resolutionTask;
            }

            return AwaitBookingUrlResolutionAsync(
                candidateUrl,
                resolutionTask);
        }

        private async Task<string> AwaitBookingUrlResolutionAsync(
            string candidateUrl,
            Task<string> resolutionTask)
        {
            try
            {
                return await resolutionTask;
            }
            finally
            {
                lock (_bookingUrlCacheLock)
                {
                    if (_bookingUrlResolveTasks.TryGetValue(
                            candidateUrl,
                            out Task<string>? currentTask) &&
                        ReferenceEquals(
                            currentTask,
                            resolutionTask))
                    {
                        _bookingUrlResolveTasks.Remove(
                            candidateUrl);
                    }
                }
            }
        }

        private async Task<string> ResolveBookingUrlCoreAsync(
            string candidateUrl,
            string scheduleUrl,
            CancellationToken stoppingToken)
        {
            await _bookingUrlResolveGate.WaitAsync(
                stoppingToken);

            try
            {
                using var request =
                    new HttpRequestMessage(
                        HttpMethod.Get,
                        candidateUrl);

                if (Uri.TryCreate(
                        scheduleUrl,
                        UriKind.Absolute,
                        out Uri? referrer))
                {
                    request.Headers.Referrer =
                        referrer;
                }

                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue(
                        "text/html"));
                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue(
                        "application/xhtml+xml"));

                using HttpResponseMessage response =
                    await _voxHttpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        stoppingToken);

                string responseRequestUrl =
                    NormalizeGuestBookingUrl(
                        response.RequestMessage?
                            .RequestUri?
                            .AbsoluteUri ??
                        string.Empty);

                string locationUrl =
                    BuildAbsoluteBookingUrl(
                        candidateUrl,
                        response.Headers.Location?
                            .ToString() ??
                        string.Empty);

                string resolvedUrl =
                    SelectFinalGuestBookingUrl(
                        responseRequestUrl,
                        locationUrl);

                if (string.IsNullOrWhiteSpace(
                        resolvedUrl))
                {
                    string responseBody =
                        await ReadBookingResponseBodySafelyAsync(
                            response,
                            stoppingToken);

                    resolvedUrl =
                        ExtractBookingUrlFromHtml(
                            responseBody,
                            response.RequestMessage?
                                .RequestUri ??
                            new Uri(
                                candidateUrl));
                }

                // Never replace a valid current VOX showtime link with an
                // empty string merely because a server-side /guest redirect
                // was not exposed to HttpClient.
                resolvedUrl =
                    SelectUsableBookingUrl(
                        resolvedUrl,
                        candidateUrl);

                int successCacheMinutes =
                    ReadIntegerSetting(
                        "Vox:BookingUrlCacheMinutes",
                        defaultValue: 15,
                        minimumValue: 1,
                        maximumValue: 60);

                int fallbackCacheSeconds =
                    ReadIntegerSetting(
                        "Vox:BookingUrlFailureCacheSeconds",
                        defaultValue: 5,
                        minimumValue: 1,
                        maximumValue: 60);

                DateTime expiresUtc =
                    DateTime.UtcNow.Add(
                        IsFinalGuestBookingUrl(
                            resolvedUrl)
                            ? TimeSpan.FromMinutes(
                                successCacheMinutes)
                            : TimeSpan.FromSeconds(
                                fallbackCacheSeconds));

                lock (_bookingUrlCacheLock)
                {
                    _bookingUrlCache[candidateUrl] =
                        new BookingUrlCacheEntry(
                            resolvedUrl,
                            expiresUtc);
                }

                if (!IsFinalGuestBookingUrl(
                        resolvedUrl))
                {
                    _logger.LogDebug(
                        "VOX did not expose a final /guest redirect for {CandidateUrl}. " +
                        "The original live VOX booking link remains visible.",
                        candidateUrl);
                }

                return resolvedUrl;
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string fallbackUrl =
                    GetSafeBookingUrlFallback(
                        candidateUrl);

                if (!string.IsNullOrWhiteSpace(
                        fallbackUrl))
                {
                    _logger.LogDebug(
                        ex,
                        "Could not upgrade {CandidateUrl} to a final /guest URL. " +
                        "The live VOX booking link remains available.",
                        candidateUrl);

                    return fallbackUrl;
                }

                _logger.LogWarning(
                    ex,
                    "Could not resolve a usable VOX booking URL for {CandidateUrl}.",
                    candidateUrl);

                return string.Empty;
            }
            finally
            {
                _bookingUrlResolveGate.Release();
            }
        }

        private static async Task<string> ReadBookingResponseBodySafelyAsync(
            HttpResponseMessage response,
            CancellationToken stoppingToken)
        {
            try
            {
                string body =
                    await response.Content.ReadAsStringAsync(
                        stoppingToken);

                const int maximumBodyLength =
                    250000;

                return body.Length <=
                       maximumBodyLength
                    ? body
                    : body.Substring(
                        0,
                        maximumBodyLength);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractBookingUrlFromHtml(
            string html,
            Uri baseUri)
        {
            if (string.IsNullOrWhiteSpace(
                    html))
            {
                return string.Empty;
            }

            string normalizedHtml =
                WebUtility.HtmlDecode(
                    html)
                    .Replace(
                        "\\/",
                        "/")
                    .Replace(
                        "\\u002F",
                        "/",
                        StringComparison.OrdinalIgnoreCase);

            MatchCollection matches =
                Regex.Matches(
                    normalizedHtml,
                    @"(?<url>https?://[^\s""'<>\\]+/booking/[^\s""'<>\\]+|/booking/[A-Za-z0-9_\-./]+(?:\?[^\s""'<>\\]*)?)",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            string fallback =
                string.Empty;

            foreach (Match match in
                     matches)
            {
                string value =
                    TrimBookingUrlPunctuation(
                        BuildAbsoluteBookingUrl(
                            baseUri.AbsoluteUri,
                            match.Groups["url"].Value));

                if (IsFinalGuestBookingUrl(
                        value))
                {
                    return value;
                }

                if (string.IsNullOrWhiteSpace(
                        fallback) &&
                    IsSafeVoxBookingUrl(
                        value))
                {
                    fallback =
                        value;
                }
            }

            return fallback;
        }

        private static string SelectFinalGuestBookingUrl(
            params string[] candidates)
        {
            foreach (string candidate in
                     candidates)
            {
                string normalized =
                    NormalizeGuestBookingUrl(
                        candidate);

                if (IsFinalGuestBookingUrl(
                        normalized))
                {
                    return normalized;
                }
            }

            return string.Empty;
        }

        private static string SelectUsableBookingUrl(
            string resolvedUrl,
            string originalUrl)
        {
            string normalizedResolved =
                NormalizeGuestBookingUrl(
                    resolvedUrl);

            if (IsSafeVoxBookingUrl(
                    normalizedResolved))
            {
                return normalizedResolved;
            }

            return GetSafeBookingUrlFallback(
                originalUrl);
        }

        private static string GetSafeBookingUrlFallback(
            string value)
        {
            string normalized =
                NormalizeGuestBookingUrl(
                    value);

            return IsSafeVoxBookingUrl(
                    normalized)
                ? normalized
                : string.Empty;
        }

        private static string BuildAbsoluteBookingUrl(
            string baseUrl,
            string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return string.Empty;
            }

            value =
                value.Trim();

            if (Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out Uri? absolute))
            {
                return NormalizeGuestBookingUrl(
                    absolute.AbsoluteUri);
            }

            if (Uri.TryCreate(
                    baseUrl,
                    UriKind.Absolute,
                    out Uri? baseUri) &&
                Uri.TryCreate(
                    baseUri,
                    value,
                    out Uri? combined))
            {
                return NormalizeGuestBookingUrl(
                    combined.AbsoluteUri);
            }

            return string.Empty;
        }

        private static string TrimBookingUrlPunctuation(
            string value)
        {
            return value.TrimEnd(
                '.',
                ',',
                ';',
                ':',
                ')',
                ']',
                '}');
        }

        private void RemoveExpiredBookingUrlCacheEntriesNoLock()
        {
            if (_bookingUrlCache.Count == 0)
            {
                return;
            }

            DateTime nowUtc =
                DateTime.UtcNow;

            string[] expiredKeys =
                _bookingUrlCache
                    .Where(item =>
                        item.Value.ExpiresUtc <=
                        nowUtc)
                    .Select(item =>
                        item.Key)
                    .ToArray();

            foreach (string expiredKey in
                     expiredKeys)
            {
                _bookingUrlCache.Remove(
                    expiredKey);
            }
        }

        private static bool IsSafeVoxBookingUrl(
            string value)
        {
            if (!Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out Uri? uri) ||
                !string.Equals(
                    uri.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsVoxCinemaHost(
                    uri.Host) ||
                uri.AbsolutePath.Contains(
                    "/processing",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] segments =
                uri.AbsolutePath
                    .Split(
                        new[]
                        {
                            '/'
                        },
                        StringSplitOptions.RemoveEmptyEntries);

            int bookingIndex =
                Array.FindIndex(
                    segments,
                    segment =>
                        string.Equals(
                            segment,
                            "booking",
                            StringComparison.OrdinalIgnoreCase));

            return bookingIndex >= 0 &&
                   bookingIndex + 1 <
                   segments.Length &&
                   !string.IsNullOrWhiteSpace(
                       segments[bookingIndex + 1]);
        }

        private static bool IsVoxCinemaHost(
            string host)
        {
            return string.Equals(
                       host,
                       "voxcinemas.com",
                       StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(
                       ".voxcinemas.com",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFinalGuestBookingUrl(
            string value)
        {
            if (!IsSafeVoxBookingUrl(
                    value) ||
                !Uri.TryCreate(
                    value,
                    UriKind.Absolute,
                    out Uri? uri))
            {
                return false;
            }

            string[] segments =
                uri.AbsolutePath
                    .Split(
                        new[]
                        {
                            '/'
                        },
                        StringSplitOptions.RemoveEmptyEntries);

            int bookingIndex =
                Array.FindIndex(
                    segments,
                    segment =>
                        string.Equals(
                            segment,
                            "booking",
                            StringComparison.OrdinalIgnoreCase));

            if (bookingIndex < 0 ||
                bookingIndex + 2 >=
                segments.Length ||
                !string.Equals(
                    segments[bookingIndex + 2],
                    "guest",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string bookingValue =
                segments[bookingIndex + 1];

            int separatorIndex =
                bookingValue.IndexOf(
                    '_');

            // Do not require Guid.TryParse here. VOX may change the identifier
            // format while the final /booking/{value}/guest route remains valid.
            return separatorIndex > 0 &&
                   separatorIndex <
                   bookingValue.Length - 1;
        }
    }
}
