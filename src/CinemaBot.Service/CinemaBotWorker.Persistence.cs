using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CinemaBot.Service
{
    /// <summary>
    /// Small PostgreSQL-backed JSON state store used to preserve the existing
    /// application's state files without forcing a large domain-model rewrite.
    /// Each legacy JSON file is stored as one JSONB document keyed by filename.
    /// </summary>
    internal sealed class PostgresStateStore
    {
        private const string SchemaSql = @"
CREATE TABLE IF NOT EXISTS cinemabot_state
(
    state_key   text PRIMARY KEY,
    state_value jsonb NOT NULL,
    updated_at  timestamptz NOT NULL DEFAULT now()
);";

        private readonly string _connectionString;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, object> _stateLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public bool IsEnabled { get; }

        public PostgresStateStore(
            IConfiguration configuration,
            ILogger logger)
        {
            _logger = logger;

            bool explicitlyEnabled =
                bool.TryParse(
                    configuration["Storage:UsePostgres"],
                    out bool parsedEnabled) &&
                parsedEnabled;

            string rawConnectionString =
                (configuration["DATABASE_URL"] ??
                 configuration["Database:ConnectionString"] ??
                 string.Empty)
                .Trim();

            IsEnabled =
                explicitlyEnabled ||
                !string.IsNullOrWhiteSpace(rawConnectionString);

            if (IsEnabled && string.IsNullOrWhiteSpace(rawConnectionString))
            {
                throw new InvalidOperationException(
                    "PostgreSQL persistence is enabled, but DATABASE_URL / Database:ConnectionString is missing.");
            }

            _connectionString =
                IsEnabled
                    ? NormalizeConnectionString(rawConnectionString)
                    : string.Empty;
        }

        public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            if (!IsEnabled)
            {
                return;
            }

            Exception? lastException = null;

            for (int attempt = 1; attempt <= 10; attempt++)
            {
                try
                {
                    await using var connection =
                        new NpgsqlConnection(_connectionString);

                    await connection.OpenAsync(cancellationToken);

                    await using var command =
                        new NpgsqlCommand(SchemaSql, connection);

                    await command.ExecuteNonQueryAsync(cancellationToken);

                    _logger.LogInformation(
                        "PostgreSQL persistence is ready. State table: cinemabot_state.");

                    return;
                }
                catch (Exception ex) when (
                    !(ex is OperationCanceledException &&
                      cancellationToken.IsCancellationRequested))
                {
                    lastException = ex;

                    _logger.LogWarning(
                        ex,
                        "Could not initialize PostgreSQL persistence (attempt {Attempt}/10).",
                        attempt);

                    await Task.Delay(
                        TimeSpan.FromSeconds(Math.Min(attempt * 2, 15)),
                        cancellationToken);
                }
            }

            throw new InvalidOperationException(
                "CinemaBot could not initialize PostgreSQL persistence after multiple attempts.",
                lastException);
        }

        public bool TryGetJson(
            string key,
            out string json)
        {
            json = string.Empty;

            if (!IsEnabled)
            {
                return false;
            }

            using var connection =
                new NpgsqlConnection(_connectionString);

            connection.Open();

            using var command =
                new NpgsqlCommand(
                    "SELECT state_value::text FROM cinemabot_state WHERE state_key = @key;",
                    connection);

            command.Parameters.AddWithValue("key", key);

            object? value = command.ExecuteScalar();

            if (value is null || value == DBNull.Value)
            {
                return false;
            }

            json = Convert.ToString(value) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(json);
        }

        public void SetJson(
            string key,
            string json)
        {
            if (!IsEnabled)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            // Validate before sending data to jsonb so a malformed state document
            // never replaces a previously valid document.
            using (JsonDocument.Parse(json))
            {
            }

            object stateLock =
                _stateLocks.GetOrAdd(
                    key,
                    _ => new object());

            lock (stateLock)
            {
                using var connection =
                    new NpgsqlConnection(_connectionString);

                connection.Open();

                using var command =
                    new NpgsqlCommand(
                        @"
INSERT INTO cinemabot_state (state_key, state_value, updated_at)
VALUES (@key, @value, now())
ON CONFLICT (state_key)
DO UPDATE SET
    state_value = EXCLUDED.state_value,
    updated_at = now();",
                        connection);

                command.Parameters.AddWithValue("key", key);
                command.Parameters.Add(
                    new NpgsqlParameter("value", NpgsqlDbType.Jsonb)
                    {
                        Value = json
                    });

                command.ExecuteNonQuery();
            }
        }

        private static string NormalizeConnectionString(string raw)
        {
            if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
                {
                    throw new InvalidOperationException(
                        "DATABASE_URL is not a valid PostgreSQL URI.");
                }

                string username = string.Empty;
                string password = string.Empty;

                if (!string.IsNullOrWhiteSpace(uri.UserInfo))
                {
                    string[] credentials = uri.UserInfo.Split(
                        ':',
                        2,
                        StringSplitOptions.None);

                    username =
                        Uri.UnescapeDataString(credentials[0]);

                    if (credentials.Length > 1)
                    {
                        password =
                            Uri.UnescapeDataString(credentials[1]);
                    }
                }

                string database =
                    Uri.UnescapeDataString(
                        uri.AbsolutePath.TrimStart('/'));

                var builder =
                    new NpgsqlConnectionStringBuilder
                    {
                        Host = uri.Host,
                        Port = uri.IsDefaultPort
                            ? 5432
                            : uri.Port,
                        Username = username,
                        Password = password,
                        Database = database,
                        ApplicationName = "CinemaBot",
                        Pooling = true,
                        SslMode = SslMode.Require
                    };

                string query = uri.Query.TrimStart('?');
                if (!string.IsNullOrWhiteSpace(query))
                {
                    foreach (string item in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] pair = item.Split('=', 2);
                        if (pair.Length != 2)
                        {
                            continue;
                        }

                        string name =
                            Uri.UnescapeDataString(pair[0]);
                        string value =
                            Uri.UnescapeDataString(pair[1]);

                        if (string.Equals(name, "sslmode", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Enum.TryParse(value, true, out SslMode sslMode))
                            {
                                builder.SslMode = sslMode;
                            }
                        }
                    }
                }

                return builder.ConnectionString;
            }

            // Also allow a normal Npgsql connection string for local development.
            return raw;
        }
    }

    public partial class CinemaBotWorker
    {
        private readonly PostgresStateStore _stateStore;

        private async Task InitializePersistenceAsync(
            CancellationToken stoppingToken)
        {
            if (!_stateStore.IsEnabled)
            {
                _logger.LogInformation(
                    "Persistent state storage is using local JSON files. Set DATABASE_URL to enable PostgreSQL.");

                LoadPersistentScalarState();
                return;
            }

            await _stateStore.EnsureSchemaAsync(stoppingToken);

            LoadPersistentScalarState();
        }

        private bool TryLoadPersistentJson(
            string stateKey,
            string legacyFilePath,
            out string json)
        {
            json = string.Empty;

            if (_stateStore.IsEnabled)
            {
                if (_stateStore.TryGetJson(stateKey, out json))
                {
                    return true;
                }
            }

            try
            {
                if (!File.Exists(legacyFilePath))
                {
                    return false;
                }

                json =
                    File.ReadAllText(
                        legacyFilePath,
                        Encoding.UTF8);

                if (_stateStore.IsEnabled &&
                    !string.IsNullOrWhiteSpace(json))
                {
                    // One-time migration path for old Windows/local JSON files.
                    _stateStore.SetJson(stateKey, json);
                    _logger.LogInformation(
                        "Migrated legacy state file {FileName} into PostgreSQL.",
                        Path.GetFileName(legacyFilePath));
                }

                return !string.IsNullOrWhiteSpace(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not load persistent state {StateKey}.",
                    stateKey);
                return false;
            }
        }

        private void SavePersistentJson(
            string stateKey,
            string legacyFilePath,
            string json)
        {
            if (_stateStore.IsEnabled)
            {
                _stateStore.SetJson(stateKey, json);
                return;
            }

            WriteTextAtomically(
                legacyFilePath,
                json);
        }

        private static void WriteTextAtomically(
            string path,
            string text)
        {
            string? directory =
                Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = path + ".tmp";

            File.WriteAllText(
                temporaryPath,
                text,
                Encoding.UTF8);

            File.Move(
                temporaryPath,
                path,
                true);
        }

        private void LoadPersistentScalarState()
        {
            // The Telegram update offset is not user data, but persisting it
            // prevents already-consumed updates from being replayed after a restart.
            if (TryLoadPersistentJson(
                    "telegram-update-offset.json",
                    Path.Combine(_dataDirectory, "telegram-update-offset.json"),
                    out string offsetJson) &&
                long.TryParse(offsetJson, out long offset))
            {
                _telegramUpdateOffset = Math.Max(0, offset);
            }
        }

        private void SaveTelegramUpdateOffset()
        {
            try
            {
                SavePersistentJson(
                    "telegram-update-offset.json",
                    Path.Combine(_dataDirectory, "telegram-update-offset.json"),
                    _telegramUpdateOffset.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Could not persist Telegram update offset.");
            }
        }
    }
}
