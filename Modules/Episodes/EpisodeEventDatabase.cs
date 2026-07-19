using System.IO;
using Microsoft.Data.Sqlite;

namespace AvatarBuilder.Modules.Episodes;

public sealed class EpisodeEventDatabase
{
    private const string EventDataFolderName = "EventData";
    private const string DatabaseFileName = "episode_events.sqlite";

    static EpisodeEventDatabase()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    public string GetDatabasePath(string outputFolder)
    {
        return Path.Combine(outputFolder, EventDataFolderName, DatabaseFileName);
    }

    public void SaveEvent(string outputFolder, AvatarBuilderEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.EventFolder))
        {
            return;
        }

        var path = GetDatabasePath(outputFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? outputFolder);
        using var connection = Open(path);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events (
                event_id,
                started_at,
                ended_at,
                event_name,
                average_motion,
                notes,
                event_folder,
                video_file,
                start_snapshot,
                end_snapshot,
                files,
                created_at
            )
            VALUES (
                $event_id,
                $started_at,
                $ended_at,
                $event_name,
                $average_motion,
                $notes,
                $event_folder,
                $video_file,
                $start_snapshot,
                $end_snapshot,
                $files,
                $created_at
            )
            ON CONFLICT(event_id) DO UPDATE SET
                started_at = excluded.started_at,
                ended_at = excluded.ended_at,
                event_name = excluded.event_name,
                average_motion = excluded.average_motion,
                notes = excluded.notes,
                event_folder = excluded.event_folder,
                video_file = excluded.video_file,
                start_snapshot = excluded.start_snapshot,
                end_snapshot = excluded.end_snapshot,
                files = excluded.files;
            """;
        command.Parameters.AddWithValue("$event_id", item.EventId);
        command.Parameters.AddWithValue("$started_at", item.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$ended_at", item.EndedAt?.ToString("O") ?? "");
        command.Parameters.AddWithValue("$event_name", item.Event);
        command.Parameters.AddWithValue("$average_motion", item.AvgMotion);
        command.Parameters.AddWithValue("$notes", item.Notes);
        command.Parameters.AddWithValue("$event_folder", item.EventFolder);
        command.Parameters.AddWithValue("$video_file", item.VideoFile);
        command.Parameters.AddWithValue("$start_snapshot", item.StartSnapshot);
        command.Parameters.AddWithValue("$end_snapshot", item.EndSnapshot);
        command.Parameters.AddWithValue("$files", item.File);
        command.Parameters.AddWithValue("$created_at", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<AvatarBuilderEvent> LoadEventsForDate(string outputFolder, DateTime date)
    {
        var path = GetDatabasePath(outputFolder);
        if (!File.Exists(path))
        {
            return [];
        }

        using var connection = Open(path);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        var start = date.Date;
        var end = start.AddDays(1);
        command.CommandText = """
            SELECT event_id, started_at, ended_at, event_name, average_motion, notes, event_folder, video_file, start_snapshot, end_snapshot, files
            FROM events
            WHERE started_at >= $start AND started_at < $end
            ORDER BY started_at DESC, created_at DESC;
            """;
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));
        var events = new List<AvatarBuilderEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public IReadOnlyList<AvatarBuilderEvent> LoadEventsSince(string outputFolder, DateTime since)
    {
        var path = GetDatabasePath(outputFolder);
        if (!File.Exists(path))
        {
            return [];
        }

        using var connection = Open(path);
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, started_at, ended_at, event_name, average_motion, notes, event_folder, video_file, start_snapshot, end_snapshot, files
            FROM events
            WHERE started_at >= $since
            ORDER BY started_at DESC, created_at DESC;
            """;
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        var events = new List<AvatarBuilderEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public AvatarBuilderEvent? DeleteEvent(string outputFolder, string eventId)
    {
        var path = GetDatabasePath(outputFolder);
        if (string.IsNullOrWhiteSpace(eventId) || !File.Exists(path))
        {
            return null;
        }

        using var connection = Open(path);
        EnsureSchema(connection);
        var existing = LoadEvent(connection, eventId);
        if (existing is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        command.ExecuteNonQuery();
        return existing;
    }

    public IReadOnlyList<AvatarBuilderEvent> DeleteEventsOlderThan(string outputFolder, DateTime cutoff)
    {
        var path = GetDatabasePath(outputFolder);
        if (!File.Exists(path))
        {
            return [];
        }

        using var connection = Open(path);
        EnsureSchema(connection);
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT event_id, started_at, ended_at, event_name, average_motion, notes, event_folder, video_file, start_snapshot, end_snapshot, files
            FROM events
            WHERE started_at < $cutoff
            ORDER BY started_at DESC, created_at DESC;
            """;
        select.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var existing = new List<AvatarBuilderEvent>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(ReadEvent(reader));
            }
        }

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM events WHERE started_at < $cutoff;";
        delete.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        delete.ExecuteNonQuery();
        return existing;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                event_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                event_name TEXT NOT NULL,
                average_motion TEXT NOT NULL,
                notes TEXT NOT NULL,
                event_folder TEXT NOT NULL UNIQUE,
                video_file TEXT NOT NULL,
                start_snapshot TEXT NOT NULL DEFAULT '',
                end_snapshot TEXT NOT NULL DEFAULT '',
                files TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_events_started_at ON events(started_at);
            """;
        command.ExecuteNonQuery();

        EnsureFlexibleEvidenceSchema(connection);
        EnsureColumn(connection, "start_snapshot", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "end_snapshot", "TEXT NOT NULL DEFAULT ''");
    }

    private static void EnsureFlexibleEvidenceSchema(SqliteConnection connection)
    {
        var schemaSql = GetEventsTableSql(connection);
        if (string.IsNullOrWhiteSpace(schemaSql)
            || !schemaSql.Contains("video_file TEXT NOT NULL UNIQUE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var columns = GetEventColumnNames(connection);
        var videoFile = columns.Contains("video_file") ? "video_file" : "''";
        var startSnapshot = columns.Contains("start_snapshot") ? "start_snapshot" : "''";
        var endSnapshot = columns.Contains("end_snapshot") ? "end_snapshot" : "''";
        var files = columns.Contains("files") ? "files" : "''";
        var createdAt = columns.Contains("created_at") ? "created_at" : "datetime('now')";

        using var transaction = connection.BeginTransaction();
        using var create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = """
            CREATE TABLE events_new (
                event_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                event_name TEXT NOT NULL,
                average_motion TEXT NOT NULL,
                notes TEXT NOT NULL,
                event_folder TEXT NOT NULL UNIQUE,
                video_file TEXT NOT NULL,
                start_snapshot TEXT NOT NULL DEFAULT '',
                end_snapshot TEXT NOT NULL DEFAULT '',
                files TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();

        using var copy = connection.CreateCommand();
        copy.Transaction = transaction;
        copy.CommandText = $"""
            INSERT OR IGNORE INTO events_new (
                event_id,
                started_at,
                ended_at,
                event_name,
                average_motion,
                notes,
                event_folder,
                video_file,
                start_snapshot,
                end_snapshot,
                files,
                created_at
            )
            SELECT
                event_id,
                started_at,
                ended_at,
                event_name,
                average_motion,
                notes,
                event_folder,
                {videoFile},
                {startSnapshot},
                {endSnapshot},
                {files},
                {createdAt}
            FROM events;
            """;
        copy.ExecuteNonQuery();

        using var drop = connection.CreateCommand();
        drop.Transaction = transaction;
        drop.CommandText = "DROP TABLE events;";
        drop.ExecuteNonQuery();

        using var rename = connection.CreateCommand();
        rename.Transaction = transaction;
        rename.CommandText = "ALTER TABLE events_new RENAME TO events;";
        rename.ExecuteNonQuery();

        using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = "CREATE INDEX IF NOT EXISTS idx_events_started_at ON events(started_at);";
        index.ExecuteNonQuery();

        transaction.Commit();
    }

    private static string GetEventsTableSql(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'events';";
        return command.ExecuteScalar() as string ?? "";
    }

    private static HashSet<string> GetEventColumnNames(SqliteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(events);";
        using var reader = inspect.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static AvatarBuilderEvent? LoadEvent(SqliteConnection connection, string eventId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, started_at, ended_at, event_name, average_motion, notes, event_folder, video_file, start_snapshot, end_snapshot, files
            FROM events
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEvent(reader) : null;
    }

    private static AvatarBuilderEvent ReadEvent(SqliteDataReader reader)
    {
        var startedAt = DateTime.TryParse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedStarted)
            ? parsedStarted
            : DateTime.Now;
        var endedAtText = reader.GetString(2);
        var endedAt = DateTime.TryParse(endedAtText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedEnded)
            ? parsedEnded
            : (DateTime?)null;
        return new AvatarBuilderEvent
        {
            EventId = reader.GetString(0),
            StartedAt = startedAt,
            EndedAt = endedAt,
            Event = reader.GetString(3),
            AvgMotion = reader.GetString(4),
            Notes = reader.GetString(5),
            EventFolder = reader.GetString(6),
            VideoFile = reader.GetString(7),
            StartSnapshot = reader.GetString(8),
            EndSnapshot = reader.GetString(9),
            File = reader.GetString(10)
        };
    }

    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(events);";
        using (var reader = inspect.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE events ADD COLUMN {name} {definition};";
        alter.ExecuteNonQuery();
    }
}
