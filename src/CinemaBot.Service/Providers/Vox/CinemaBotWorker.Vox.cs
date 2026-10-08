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
        private async Task<DaySchedule?> TryLoadArabicScheduleAsync(
                DaySchedule englishSchedule,
                CancellationToken stoppingToken)
        {
            string arabicBaseUrl =
                (_configuration["Vox:ArabicUrl"] ??
                 "https://egy.voxcinemas.com/ar/showtimes")
                .Trim();

            if (!IsValidVoxBaseUrl(
                    arabicBaseUrl))
            {
                return null;
            }

            string arabicUrl =
                BuildVoxUrlForCinemaAndDate(
                    arabicBaseUrl,
                    englishSchedule.CinemaSlug,
                    englishSchedule.Date);

            string? html =
                await DownloadVoxPageAsync(
                    arabicUrl,
                    stoppingToken);

            if (string.IsNullOrWhiteSpace(
                    html))
            {
                return null;
            }

            string extractedCinemaName =
                ExtractSelectedCinemaName(
                    html,
                    englishSchedule.CinemaName);

            string arabicCinemaName =
                GetArabicCinemaDisplayName(
                    englishSchedule.CinemaSlug,
                    extractedCinemaName,
                    englishSchedule.CinemaName);

            var cinema =
                new CinemaOption(
                    englishSchedule.CinemaSlug,
                    arabicCinemaName);

            if (!TryExtractDaySchedule(
                    html,
                    cinema,
                    englishSchedule.Date,
                    arabicUrl,
                    out DaySchedule localizedSchedule))
            {
                return null;
            }

            return DeduplicateLocalizedSchedule(
                englishSchedule,
                localizedSchedule);
        }

        private static string ExtractSelectedCinemaName(
                string html,
                string fallback)
        {
            try
            {
                var document =
                    new HtmlDocument();

                document.LoadHtml(
                    html);

                HtmlNode? checkedInput =
                    document.DocumentNode.SelectSingleNode(
                        "//input[" +
                        "translate(@name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', " +
                        "'abcdefghijklmnopqrstuvwxyz')='c' and @checked]");

                HtmlNode? label =
                    checkedInput?.Ancestors(
                        "label")
                        .FirstOrDefault();

                string selectedName =
                    NormalizeDisplayText(
                        HtmlEntity.DeEntitize(
                            label?.SelectSingleNode(
                                ".//span")?.InnerText ??
                            string.Empty));

                if (!string.IsNullOrWhiteSpace(
                        selectedName))
                {
                    return selectedName;
                }

                HtmlNode? heading =
                    document.DocumentNode.SelectSingleNode(
                        "//h3[" +
                        "contains(" +
                        "concat(' ', normalize-space(@class), ' '), " +
                        "' highlight ')" +
                        "]");

                selectedName =
                    NormalizeDisplayText(
                        HtmlEntity.DeEntitize(
                            heading?.InnerText ??
                            string.Empty));

                return string.IsNullOrWhiteSpace(
                           selectedName)
                    ? fallback
                    : selectedName;
            }
            catch
            {
                return fallback;
            }
        }

        private static List<string> BuildArabicDayTelegramMessages(
                DaySchedule schedule,
                int maximumLength)
        {
            int movieCount =
                schedule.Movies.Length;

            int availableCount =
                schedule.Movies.Sum(
                    movie =>
                        movie.Halls.Sum(
                            hall =>
                                hall.Showtimes.Count(
                                    showtime =>
                                        showtime.IsAvailable)));

            int soldOutCount =
                schedule.Movies.Sum(
                    movie =>
                        movie.Halls.Sum(
                            hall =>
                                hall.Showtimes.Count(
                                    showtime =>
                                        !showtime.IsAvailable)));

            string header =
                "🚨 <b>تحديث جديد في ڤوكس سينما</b> 🚨\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "🏢 <b>" +
                EscapeTelegramHtml(
                    schedule.CinemaName) +
                "</b>\n" +
                "📅 <b>" +
                EscapeTelegramHtml(
                    schedule.Date.ToString(
                        "dddd، dd MMMM yyyy",
                        new CultureInfo(
                            "ar-EG"))) +
                "</b>\n" +
                "🎬 " +
                movieCount +
                " أفلام  •  🟢 " +
                availableCount +
                " متاح  •  🔴 " +
                soldOutCount +
                " مكتمل\n" +
                "━━━━━━━━━━━━━━━━━━━━\n" +
                "⚡ تم رصد تغيير جديد في الأفلام أو القاعات أو المواعيد أو حالة الحجز.\n";

            var messages =
                new List<string>();

            var current =
                new StringBuilder(
                    header);

            foreach (MovieSchedule movie in
                     schedule.Movies)
            {
                string block =
                    BuildArabicMovieScheduleBlock(
                        movie);

                AppendDigestBlock(
                    messages,
                    current,
                    header,
                    block,
                    maximumLength);
            }

            string linkBlock =
                "\n━━━━━━━━━━━━━━━━━━━━\n" +
                "🎟 <a href=\"" +
                EscapeTelegramHtml(
                    schedule.Url) +
                "\"><b>افتح جدول السينما واحجز الآن</b></a>\n" +
                "⏱ <i>يتم فحص التغييرات كل دقيقة.</i>\n";

            AppendDigestBlock(
                messages,
                current,
                header,
                linkBlock,
                maximumLength);

            if (current.Length > 0)
            {
                messages.Add(
                    current.ToString());
            }

            return messages;
        }

        private static string BuildArabicMovieScheduleBlock(
                MovieSchedule movie)
        {
            var builder =
                new StringBuilder();

            builder.AppendLine();
            builder.AppendLine(
                "╭──────────────────");

            builder.AppendLine(
                "🎞 <b>" +
                EscapeTelegramHtml(
                    movie.Title) +
                "</b>");

            foreach (HallSchedule hall in
                     movie.Halls)
            {
                builder.AppendLine(
                    "│");

                builder.AppendLine(
                    "│  🏛 <b>" +
                    EscapeTelegramHtml(
                        hall.Name) +
                    "</b>");

                foreach (ShowtimeSchedule showtime in
                         hall.Showtimes)
                {
                    string time =
                        EscapeTelegramHtml(
                            showtime.Time);

                    if (showtime.IsAvailable)
                    {
                        builder.Append(
                            "│     🟢 <b>" +
                            time +
                            "</b>");

                        if (!string.IsNullOrWhiteSpace(
                                showtime.BookingUrl))
                        {
                            builder.Append(
                                "  •  <a href=\"" +
                                EscapeTelegramHtml(
                                    showtime.BookingUrl) +
                                "\"><b>احجز</b></a>");
                        }

                        builder.AppendLine();
                    }
                    else
                    {
                        builder.AppendLine(
                            "│     🔴 <b>" +
                            time +
                            "</b>  •  <b>الحجز مكتمل</b>");
                    }
                }
            }

            builder.AppendLine(
                "╰──────────────────");

            return builder.ToString();
        }

        private async Task<bool> ValidateTelegramBotAsync(
                string botToken,
                CancellationToken stoppingToken)
        {
            string apiUrl =
                $"https://api.telegram.org/bot{botToken}/getMe";

            try
            {
                using HttpResponseMessage response =
                    await _telegramHttpClient.GetAsync(
                        apiUrl,
                        stoppingToken);

                string responseBody =
                    await response.Content.ReadAsStringAsync(
                        stoppingToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "Telegram BotToken validation failed. " +
                        "HTTP {StatusCode}: {Response}",
                        (int)response.StatusCode,
                        LimitText(
                            responseBody,
                            1000));

                    return false;
                }

                try
                {
                    using JsonDocument document =
                        JsonDocument.Parse(
                            responseBody);

                    if (document.RootElement.TryGetProperty(
                            "result",
                            out JsonElement result) &&
                        result.TryGetProperty(
                            "username",
                            out JsonElement usernameElement))
                    {
                        _botUsername =
                            usernameElement.GetString() ??
                            string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Telegram getMe succeeded, but the bot username could not be read.");
                }

                _logger.LogInformation(
                    "Telegram BotToken validation succeeded for @{BotUsername}.",
                    _botUsername);

                await EnsureTelegramBotMenuAsync(
                    botToken,
                    stoppingToken);

                return true;
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
                    "Unable to validate the Telegram BotToken.");

                return false;
            }
        }

        private void ConfigureVoxHeaders()
        {
            _voxHttpClient.DefaultRequestHeaders
                .UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                    "AppleWebKit/537.36 (KHTML, like Gecko) " +
                    "Chrome/138.0.0.0 Safari/537.36");

            _voxHttpClient.DefaultRequestHeaders.Accept.Clear();

            _voxHttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "text/html"));

            _voxHttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/xhtml+xml"));

            _voxHttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/xml",
                    0.9));

            _voxHttpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "*/*",
                    0.8));

            _voxHttpClient.DefaultRequestHeaders
                .AcceptLanguage.ParseAdd(
                    "en-US,en;q=0.9");

            _voxHttpClient.DefaultRequestHeaders.CacheControl =
                new CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true
                };

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "Upgrade-Insecure-Requests",
                    "1");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-ch-ua",
                    "\"Not)A;Brand\";v=\"8\", \"Chromium\";v=\"138\", " +
                    "\"Google Chrome\";v=\"138\"");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-ch-ua-mobile",
                    "?0");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-ch-ua-platform",
                    "\"Windows\"");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-fetch-dest",
                    "document");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-fetch-mode",
                    "navigate");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-fetch-site",
                    "same-origin");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "sec-fetch-user",
                    "?1");

            _voxHttpClient.DefaultRequestHeaders
                .TryAddWithoutValidation(
                    "priority",
                    "u=0, i");
        }

        private async Task<string?> DownloadVoxPageAsync(
                string url,
                CancellationToken stoppingToken)
        {
            const int maximumAttempts = 3;

            for (int attempt = 1;
                 attempt <= maximumAttempts;
                 attempt++)
            {
                try
                {
                    _logger.LogInformation(
                        "Checking VOX URL {Url}. Attempt {Attempt}/{MaximumAttempts}.",
                        url,
                        attempt,
                        maximumAttempts);

                    using var request =
                        new HttpRequestMessage(
                            HttpMethod.Get,
                            url);

                    request.Headers.Referrer =
                        new Uri(
                            "https://egy.voxcinemas.com/");

                    request.Headers.TryAddWithoutValidation(
                        "Pragma",
                        "no-cache");

                    using HttpResponseMessage response =
                        await _voxHttpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            stoppingToken);

                    LogPotentialWafHeaders(
                        response);

                    string html =
                        await response.Content.ReadAsStringAsync(
                            stoppingToken);

                    if (!string.IsNullOrWhiteSpace(
                            html) &&
                        LooksLikeBotChallengePage(
                            html))
                    {
                        SaveDebugSnapshot(
                            html);

                        _logger.LogError(
                            "VOX returned a bot-protection/challenge page. " +
                            "HTTP {StatusCode}.",
                            (int)response.StatusCode);

                        if (attempt <
                            maximumAttempts)
                        {
                            await WaitBeforeRetryAsync(
                                attempt,
                                stoppingToken);
                        }

                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        SaveDebugSnapshot(
                            html);

                        _logger.LogWarning(
                            "VOX returned HTTP {StatusCode}: {Response}",
                            (int)response.StatusCode,
                            LimitText(
                                html,
                                500));

                        if (attempt <
                            maximumAttempts)
                        {
                            await WaitBeforeRetryAsync(
                                attempt,
                                stoppingToken);
                        }

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(
                            html))
                    {
                        _logger.LogWarning(
                            "VOX returned an empty response.");

                        if (attempt <
                            maximumAttempts)
                        {
                            await WaitBeforeRetryAsync(
                                attempt,
                                stoppingToken);
                        }

                        continue;
                    }

                    return html;
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                    when (ex.InnerException is
                          SocketException socketException)
                {
                    _logger.LogWarning(
                        ex,
                        "VOX forcibly closed the connection. " +
                        "Socket error: {SocketErrorCode}.",
                        socketException.SocketErrorCode);
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "VOX request timed out.");
                }
                catch (AuthenticationException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "TLS/SSL handshake with VOX failed.");
                }
                catch (HttpRequestException ex)
                    when (ex.InnerException is
                          AuthenticationException)
                {
                    _logger.LogWarning(
                        ex,
                        "TLS/SSL handshake with VOX failed.");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "VOX HTTP request failed.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "VOX request failed.");
                }

                if (attempt <
                    maximumAttempts)
                {
                    await WaitBeforeRetryAsync(
                        attempt,
                        stoppingToken);
                }
            }

            return null;
        }

        private static string MakeAbsoluteUrl(
                string pageUrl,
                string href)
        {
            if (string.IsNullOrWhiteSpace(
                    href))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(
                    href,
                    UriKind.Absolute,
                    out Uri? absoluteUrl))
            {
                return absoluteUrl.AbsoluteUri;
            }

            if (Uri.TryCreate(
                    new Uri(pageUrl),
                    href,
                    out Uri? combinedUrl))
            {
                return combinedUrl.AbsoluteUri;
            }

            return href;
        }

        private static string NormalizeDisplayText(
                string value)
        {
            if (string.IsNullOrWhiteSpace(
                    value))
            {
                return string.Empty;
            }

            string normalized =
                HtmlEntity.DeEntitize(
                    value)
                    .Replace('–', '-')
                    .Replace('—', '-')
                    .Replace('‑', '-')
                    .Replace('−', '-');

            return string.Join(
                " ",
                normalized.Split(
                    new[]
                    {
                        ' ',
                        '\r',
                        '\n',
                        '\t'
                    },
                    StringSplitOptions.RemoveEmptyEntries))
                .Trim();
        }

        private static bool LooksLikeBotChallengePage(
                string html)
        {
            return BotChallengeMarkers.Any(
                marker =>
                    html.Contains(
                        marker,
                        StringComparison.OrdinalIgnoreCase));
        }

        private void LogPotentialWafHeaders(
                HttpResponseMessage response)
        {
            if (!_logger.IsEnabled(
                    LogLevel.Debug))
            {
                return;
            }

            var found =
                new List<string>();

            foreach (string headerName in
                     WafRevealingHeaderNames)
            {
                IEnumerable<string>? values =
                    null;

                if (response.Headers.TryGetValues(
                        headerName,
                        out IEnumerable<string>? responseValues))
                {
                    values =
                        responseValues;
                }
                else if (response.Content.Headers.TryGetValues(
                             headerName,
                             out IEnumerable<string>? contentValues))
                {
                    values =
                        contentValues;
                }

                if (values is null)
                {
                    continue;
                }

                string combined =
                    string.Join(
                        ",",
                        values);

                if (string.Equals(
                        headerName,
                        "server",
                        StringComparison.OrdinalIgnoreCase) &&
                    combined.Contains(
                        "AkamaiGHost",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(
                    headerName +
                    "=" +
                    combined);
            }

            if (found.Count > 0)
            {
                _logger.LogDebug(
                    "CDN/WAF diagnostic headers: {Headers}",
                    string.Join(
                        "; ",
                        found));
            }
        }

        private void SaveDebugSnapshot(
                string html)
        {
            try
            {
                lock (_debugFileLock)
                {
                    string debugDirectory =
                        Path.Combine(
                            AppContext.BaseDirectory,
                            "debug");

                    Directory.CreateDirectory(
                        debugDirectory);

                    string debugFilePath =
                        Path.Combine(
                            debugDirectory,
                            "last-vox-response.html");

                    File.WriteAllText(
                        debugFilePath,
                        html);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save the VOX debug snapshot.");
            }
        }

        private static async Task WaitBeforeRetryAsync(
                int attempt,
                CancellationToken stoppingToken)
        {
            int retryDelaySeconds =
                attempt * 3;

            await Task.Delay(
                TimeSpan.FromSeconds(
                    retryDelaySeconds),
                stoppingToken);
        }
    }
}
