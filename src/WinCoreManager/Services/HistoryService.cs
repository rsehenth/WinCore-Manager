using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using WinCoreManager.Localization;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public interface IHistoryService
{
    Task AddAsync(HistoryRecord record);

    Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryFilter filter);

    Task<IReadOnlyList<HistoryRecord>> RecentAsync(int count);

    Task<int> ClearAsync();

    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff);
}

public sealed class HistoryService : IHistoryService
{
    private const int SchemaVersion = 1;
    private const string UtcIndex = "ix_history_utc_timestamp";
    // Timestamps already on disk use the round-trip format, including the original offset.
    // Resolve the offset on whole seconds and keep the full fractional part for comparisons.
    private const string UtcTimestamp = "(strftime('%Y-%m-%dT%H:%M:%S', substr(timestamp, 1, 19) || substr(timestamp, 28)) || substr(timestamp, 20, 8))";
    private readonly string _databasePath;
    private readonly object _schemaGate = new();
    private bool _schemaChecked;

    public HistoryService(ISettingsService settingsService)
    {
        _databasePath = settingsService.HistoryDatabasePath;
    }

    public Task AddAsync(HistoryRecord record) => Task.Run(() =>
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO history (timestamp, type, target, details, status, exit_code, message, duration_ms, version_from, version_to)
            VALUES (@timestamp, @type, @target, @details, @status, @exit_code, @message, @duration_ms, @version_from, @version_to);
            """;
        command.Parameters.AddWithValue("@timestamp", record.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("@type", (int)record.Type);
        command.Parameters.AddWithValue("@target", record.Target);
        command.Parameters.AddWithValue("@details", record.Details);
        command.Parameters.AddWithValue("@status", (int)record.Status);
        command.Parameters.AddWithValue("@exit_code", record.ExitCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@message", record.Message);
        command.Parameters.AddWithValue("@duration_ms", record.DurationMs ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@version_from", record.VersionFrom);
        command.Parameters.AddWithValue("@version_to", record.VersionTo);
        command.ExecuteNonQuery();
    });

    public Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryFilter filter) => Task.Run<IReadOnlyList<HistoryRecord>>(() =>
    {
        var conditions = new List<string> { $"{UtcTimestamp} IS NOT NULL" };
        var parameters = new Dictionary<string, object?>();

        if (filter.Type.HasValue)
        {
            conditions.Add("type = @type");
            parameters["@type"] = (int)filter.Type.Value;
        }

        if (filter.Status.HasValue)
        {
            conditions.Add("status = @status");
            parameters["@status"] = (int)filter.Status.Value;
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            conditions.Add("(target LIKE @search ESCAPE '!' OR details LIKE @search ESCAPE '!' OR message LIKE @search ESCAPE '!')");
            var search = filter.Search.Trim().Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
            parameters["@search"] = "%" + search + "%";
        }

        parameters["@limit"] = Math.Clamp(filter.Limit, 1, 5000);

        var where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var sql = "SELECT id, timestamp, type, target, details, status, exit_code, message, duration_ms, version_from, version_to FROM history"
                  + where + $" ORDER BY {UtcTimestamp} DESC, id DESC LIMIT @limit;";

        using var connection = OpenConnection();
        var records = new List<HistoryRecord>();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                {
                    records.Add(MapRecord(reader, timestamp));
                }
            }
        }

        return records;
    });

    public Task<IReadOnlyList<HistoryRecord>> RecentAsync(int count) =>
        QueryAsync(new HistoryFilter { Limit = Math.Clamp(count, 1, 100) });

    public Task<int> ClearAsync() => Task.Run(() =>
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM history;";
        return command.ExecuteNonQuery();
    });

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => Task.Run(() =>
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM history WHERE {UtcTimestamp} < @cutoff;";
        command.Parameters.AddWithValue("@cutoff", cutoff.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        return command.ExecuteNonQuery();
    });

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            lock (_schemaGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
                var database = new FileInfo(_databasePath);
                if ((database.Exists && database.Length < 100) ||
                    (!database.Exists && (File.Exists(_databasePath + "-wal") || File.Exists(_databasePath + "-journal"))))
                {
                    throw Unavailable("Message.HistoryDatabaseDamaged");
                }

                connection.Open();
                if (!_schemaChecked || (long)Scalar(connection, "PRAGMA user_version;")! != SchemaVersion)
                {
                    EnsureSchema(connection);
                    EnableLocalWal(connection);
                    _schemaChecked = true;
                }
            }
            return connection;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            throw Unavailable("Message.HistoryDatabaseDamaged", ex);
        }
        catch
        {
            SqliteConnection.ClearPool(connection);
            connection.Dispose();
            throw;
        }
    }

    private static void EnableLocalWal(SqliteConnection connection)
    {
        var root = Path.GetPathRoot(connection.DataSource);
        if (root is { Length: 3 } && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable)
        {
            Scalar(connection, "PRAGMA journal_mode=WAL;");
        }
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = (long)command.ExecuteScalar()!;
        if (version is < 0 or > SchemaVersion)
        {
            throw Unavailable("Message.HistoryDatabaseUnsupported");
        }

        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'history';";
        var hasHistory = command.ExecuteScalar() is not null;
        if (!hasHistory)
        {
            command.CommandText = "SELECT 1 FROM sqlite_schema LIMIT 1;";
            if (version != 0 || command.ExecuteScalar() is not null)
            {
                throw Unavailable("Message.HistoryDatabaseUnsupported");
            }
        }

        if (!IsIntact(command))
        {
            command.CommandText = $"SELECT 1 FROM sqlite_schema WHERE type = 'index' AND tbl_name = 'history' AND name = '{UtcIndex}';";
            if (command.ExecuteScalar() is null)
            {
                throw Unavailable("Message.HistoryDatabaseDamaged");
            }

            // The UTC index is derived from history. Commit a rebuild only if the entire database verifies.
            command.CommandText = $"REINDEX main.{UtcIndex};";
            command.ExecuteNonQuery();
            if (!IsIntact(command))
            {
                throw Unavailable("Message.HistoryDatabaseDamaged");
            }
        }

        if (hasHistory)
        {
            MigrateHistoryColumns(command, version);
        }

        command.CommandText =
            $"""
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                type INTEGER NOT NULL,
                target TEXT NOT NULL,
                details TEXT NOT NULL DEFAULT '',
                status INTEGER NOT NULL,
                exit_code INTEGER,
                message TEXT NOT NULL DEFAULT '',
                duration_ms INTEGER,
                version_from TEXT NOT NULL DEFAULT '',
                version_to TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS {UtcIndex} ON history ({UtcTimestamp} DESC, id DESC);
            PRAGMA user_version = {SchemaVersion};
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void MigrateHistoryColumns(SqliteCommand command, long version)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        command.CommandText = "PRAGMA table_info(history);";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (!new[] { "id", "timestamp", "type", "target", "status" }.All(columns.Contains))
        {
            throw Unavailable("Message.HistoryDatabaseUnsupported");
        }

        (string Name, string Definition)[] additions =
        [
            ("details", "TEXT NOT NULL DEFAULT ''"),
            ("exit_code", "INTEGER"),
            ("message", "TEXT NOT NULL DEFAULT ''"),
            ("duration_ms", "INTEGER"),
            ("version_from", "TEXT NOT NULL DEFAULT ''"),
            ("version_to", "TEXT NOT NULL DEFAULT ''"),
        ];
        foreach (var (name, definition) in additions.Where(column => !columns.Contains(column.Name)))
        {
            if (version != 0)
            {
                throw Unavailable("Message.HistoryDatabaseUnsupported");
            }
            command.CommandText = $"ALTER TABLE history ADD COLUMN {name} {definition};";
            command.ExecuteNonQuery();
        }
    }

    private static bool IsIntact(SqliteCommand command)
    {
        command.CommandText = "PRAGMA integrity_check(1);";
        return Equals(command.ExecuteScalar(), "ok");
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private HistoryStorageException Unavailable(string key, Exception? inner = null) =>
        new(LocalizationService.Source(key, _databasePath), inner);

    private static HistoryRecord MapRecord(SqliteDataReader reader, DateTimeOffset timestamp)
    {
        return new HistoryRecord
        {
            Id = reader.GetInt64(0),
            Timestamp = timestamp,
            Type = (HistoryType)reader.GetInt32(2),
            Target = reader.GetString(3),
            Details = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Status = (HistoryStatus)reader.GetInt32(5),
            ExitCode = reader.IsDBNull(6) ? null : reader.GetInt32(6),
            Message = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            DurationMs = reader.IsDBNull(8) ? null : reader.GetInt64(8),
            VersionFrom = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            VersionTo = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
        };
    }
}

internal sealed class HistoryStorageException(string message, Exception? inner) : IOException(message, inner);
