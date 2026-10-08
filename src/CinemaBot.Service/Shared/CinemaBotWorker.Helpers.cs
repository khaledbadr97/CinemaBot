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
        private static string EscapeTelegramHtml(
                string? value)
        {
            if (string.IsNullOrEmpty(
                    value))
            {
                return string.Empty;
            }

            // Telegram HTML parse mode supports only a limited set of tags.
            // Encode dynamic text so movie names, cinema names, halls, times,
            // and URLs cannot break the generated Telegram message.
            //
            // Replace '&' first to avoid double-encoding the entities added
            // by the following replacements.
            return value
                .Replace(
                    "&",
                    "&amp;")
                .Replace(
                    "<",
                    "&lt;")
                .Replace(
                    ">",
                    "&gt;")
                .Replace(
                    "\"",
                    "&quot;");
        }

        private static string NormalizeGuestBookingUrl(
                string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return string.Empty;
            }

            string normalized =
                value.Trim();

            int queryIndex =
                normalized.IndexOfAny(
                    new[]
                    {
                        '?',
                        '#'
                    });

            string suffix =
                queryIndex >= 0
                    ? normalized.Substring(
                        queryIndex)
                    : string.Empty;

            string path =
                queryIndex >= 0
                    ? normalized.Substring(
                        0,
                        queryIndex)
                    : normalized;

            while (path.EndsWith(
                       "/processing",
                       StringComparison.OrdinalIgnoreCase))
            {
                path =
                    path.Substring(
                        0,
                        path.Length -
                        "/processing".Length);
            }

            path =
                path.TrimEnd('/');

            // Booking links must end at /guest. Query strings and fragments
            // from the intermediate VOX processing page are intentionally
            // discarded so the user never receives a processing URL.
            if (path.Contains(
                    "/booking/",
                    StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(
                    "/guest",
                    StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return path + suffix;
        }

        private static string NormalizeBotToken(
                string? configuredToken)
        {
            if (string.IsNullOrWhiteSpace(
                    configuredToken))
            {
                return string.Empty;
            }

            string token =
                string.Concat(
                    configuredToken.Where(
                        character =>
                            !char.IsWhiteSpace(
                                character)));

            if (token.StartsWith(
                    "bot",
                    StringComparison.OrdinalIgnoreCase))
            {
                token =
                    token.Substring(3);
            }

            return token;
        }

        private static bool IsValidBotTokenFormat(
                string token)
        {
            int separatorIndex =
                token.IndexOf(':');

            if (separatorIndex <= 0 ||
                separatorIndex ==
                token.Length - 1)
            {
                return false;
            }

            string botId =
                token.Substring(
                    0,
                    separatorIndex);

            string secret =
                token.Substring(
                    separatorIndex + 1);

            return botId.All(
                       char.IsDigit)
                   && secret.Length >= 20
                   && secret.All(
                       character =>
                           char.IsLetterOrDigit(
                               character) ||
                           character == '_' ||
                           character == '-');
        }

        private bool ReadBooleanSetting(
                string key,
                bool defaultValue)
        {
            string? configuredValue =
                _configuration[key];

            return bool.TryParse(
                       configuredValue,
                       out bool parsedValue)
                ? parsedValue
                : defaultValue;
        }

        private int ReadIntegerSetting(
                string key,
                int defaultValue,
                int minimumValue,
                int maximumValue)
        {
            if (!int.TryParse(
                    _configuration[key],
                    out int value))
            {
                return defaultValue;
            }

            return Math.Max(
                minimumValue,
                Math.Min(
                    maximumValue,
                    value));
        }

        private bool IsFutureShowtime(
                DateTime scheduleDate,
                string showtimeText,
                DateTime nowLocal)
        {
            DateTime? showtimeDateTime =
                TryBuildShowtimeDateTime(
                    scheduleDate,
                    showtimeText);

            if (showtimeDateTime.HasValue)
            {
                return showtimeDateTime.Value >
                       nowLocal;
            }

            // If the time cannot be parsed, a future schedule date is still
            // considered relevant. For today, fail closed to avoid noisy
            // Hall Removed notifications after a session has already passed.
            return scheduleDate.Date >
                   nowLocal.Date;
        }

        private DateTime? TryBuildShowtimeDateTime(
                DateTime scheduleDate,
                string showtimeText)
        {
            if (string.IsNullOrWhiteSpace(
                    showtimeText))
            {
                return null;
            }

            Match match =
                Regex.Match(
                    showtimeText,
                    @"(?<time>\d{1,2}(?::\d{2})?\s*(?:a\.?m\.?|p\.?m\.?))",
                    RegexOptions.IgnoreCase |
                    RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                return null;
            }

            string value =
                match.Groups["time"].Value
                    .Replace(".", string.Empty)
                    .Replace(" ", string.Empty)
                    .ToUpperInvariant();

            string[] formats =
            {
                "h:mmtt",
                "htt",
                "hh:mmtt",
                "h:mm tt",
                "hh:mm tt"
            };

            if (!DateTime.TryParseExact(
                    value,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTime parsed))
            {
                return null;
            }

            DateTime result =
                scheduleDate.Date.Add(
                    parsed.TimeOfDay);

            int afterMidnightCutoffHour =
                ReadIntegerSetting(
                    "Schedule:AfterMidnightCutoffHour",
                    defaultValue: 6,
                    minimumValue: 0,
                    maximumValue: 12);

            // VOX groups early-morning screenings under the previous cinema
            // business day. For example, 12:30 AM on the 5-Aug schedule is
            // treated as 6-Aug 12:30 AM.
            if (parsed.Hour <
                afterMidnightCutoffHour)
            {
                result =
                    result.AddDays(1);
            }

            return result;
        }

        private void RequestImmediateScan(
                bool force = false)
        {
            int debounceSeconds =
                ReadIntegerSetting(
                    "Performance:ImmediateRefreshDebounceSeconds",
                    defaultValue: 8,
                    minimumValue: 2,
                    maximumValue: 60);

            lock (_scanRefreshLock)
            {
                DateTime nowUtc =
                    DateTime.UtcNow;

                if (!force &&
                    nowUtc -
                    _lastImmediateScanRequestUtc <
                    TimeSpan.FromSeconds(
                        debounceSeconds))
                {
                    return;
                }

                _lastImmediateScanRequestUtc =
                    nowUtc;

                if (_scanWakeSignal.CurrentCount == 0)
                {
                    _scanWakeSignal.Release();
                }
            }
        }

        private async Task WaitForNextScanAsync(
                TimeSpan delay,
                CancellationToken stoppingToken)
        {
            using var waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);

            Task delayTask =
                Task.Delay(
                    delay,
                    waitCancellation.Token);

            Task signalTask =
                _scanWakeSignal.WaitAsync(
                    waitCancellation.Token);

            Task completed =
                await Task.WhenAny(
                    delayTask,
                    signalTask);

            waitCancellation.Cancel();

            try
            {
                await completed;
            }
            catch (OperationCanceledException)
                when (!stoppingToken.IsCancellationRequested)
            {
                // The other wait completed first.
            }
        }

        private string BuildLiveStatusLine(
                string language)
        {
            bool arabic =
                string.Equals(
                    language,
                    "ar",
                    StringComparison.OrdinalIgnoreCase);

            DateTime lastScanUtc =
                _lastSuccessfulScheduleScanUtc;

            if (lastScanUtc ==
                DateTime.MinValue)
            {
                return arabic
                    ? "⚡ جاري تحميل البيانات المباشرة..."
                    : "⚡ Loading live data...";
            }

            int ageSeconds =
                Math.Max(
                    0,
                    (int)Math.Floor(
                        (DateTime.UtcNow -
                         lastScanUtc).TotalSeconds));

            if (ageSeconds < 60)
            {
                return arabic
                    ? "⚡ آخر تحديث مباشر منذ " +
                      ageSeconds +
                      " ثانية"
                    : "⚡ Live updated " +
                      ageSeconds +
                      "s ago";
            }

            int ageMinutes =
                Math.Max(
                    1,
                    ageSeconds / 60);

            return arabic
                ? "⚡ آخر تحديث مباشر منذ " +
                  ageMinutes +
                  " دقيقة"
                : "⚡ Live updated " +
                  ageMinutes +
                  "m ago";
        }

        private static string BuildMovieDetailsLoadKey(
                long chatId,
                int messageId)
        {
            return chatId.ToString(
                       CultureInfo.InvariantCulture) +
                   "|" +
                   messageId.ToString(
                       CultureInfo.InvariantCulture);
        }

        private void InvalidatePendingMovieDetailsLoad(
                long chatId,
                int messageId)
        {
            string key =
                BuildMovieDetailsLoadKey(
                    chatId,
                    messageId);

            lock (_movieDetailsLoadLock)
            {
                _movieDetailsLoadVersions.TryGetValue(
                    key,
                    out long currentVersion);

                _movieDetailsLoadVersions[key] =
                    currentVersion +
                    1;
            }
        }

        private long BeginMovieDetailsLoad(
                long chatId,
                int messageId)
        {
            string key =
                BuildMovieDetailsLoadKey(
                    chatId,
                    messageId);

            lock (_movieDetailsLoadLock)
            {
                _movieDetailsLoadVersions.TryGetValue(
                    key,
                    out long currentVersion);

                long nextVersion =
                    currentVersion +
                    1;

                _movieDetailsLoadVersions[key] =
                    nextVersion;

                return nextVersion;
            }
        }

        private bool IsMovieDetailsLoadCurrent(
                long chatId,
                int messageId,
                long version)
        {
            string key =
                BuildMovieDetailsLoadKey(
                    chatId,
                    messageId);

            lock (_movieDetailsLoadLock)
            {
                return _movieDetailsLoadVersions.TryGetValue(
                           key,
                           out long currentVersion) &&
                       currentVersion ==
                       version;
            }
        }

        private static string LimitText(
                string? value,
                int maximumLength)
        {
            if (string.IsNullOrEmpty(
                    value))
            {
                return string.Empty;
            }

            return value.Length <=
                   maximumLength
                ? value
                : value.Substring(
                    0,
                    maximumLength);
        }
    }
}
