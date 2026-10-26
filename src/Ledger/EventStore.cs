using Microsoft.Data.Sqlite;

namespace Ledger;

public sealed record StoredEvent(long Position, string Stream, long Version, LedgerEvent Event, DateTime RecordedUtc);

public interface IEventStore
{
    IReadOnlyList<StoredEvent> Read(string stream);

    /// <summary>Appends only if the stream is still at <paramref name="expectedVersion"/>.</summary>
    void Append(string stream, long expectedVersion, IReadOnlyList<LedgerEvent> events);
}

/// <summary>
/// Append-only event table in SQLite. The unique (stream, version) index is the concurrency check: two writers
/// that loaded the same version can't both append the next one.
/// </summary>
public sealed class SqliteEventStore : IEventStore
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public SqliteEventStore(string connectionString, TimeProvider? clock = null)
    {
        _connectionString = connectionString;
        _clock = clock ?? TimeProvider.System;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                position     INTEGER PRIMARY KEY AUTOINCREMENT,
                stream       TEXT    NOT NULL,
                version      INTEGER NOT NULL,
                data         TEXT    NOT NULL,
                recorded_utc TEXT    NOT NULL,
                UNIQUE (stream, version)
            );
            -- append-only: refuse edits and deletes at the database level too
            CREATE TRIGGER IF NOT EXISTS events_no_update BEFORE UPDATE ON events
                BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
            CREATE TRIGGER IF NOT EXISTS events_no_delete BEFORE DELETE ON events
                BEGIN SELECT RAISE(ABORT, 'events are append-only'); END;
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<StoredEvent> Read(string stream)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT position, version, data, recorded_utc FROM events WHERE stream = $s ORDER BY version";
        cmd.Parameters.AddWithValue("$s", stream);
        var list = new List<StoredEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StoredEvent(r.GetInt64(0), stream, r.GetInt64(1), EventJson.Deserialize(r.GetString(2)),
                DateTime.Parse(r.GetString(3)).ToUniversalTime()));
        }
        return list;
    }

    public void Append(string stream, long expectedVersion, IReadOnlyList<LedgerEvent> events)
    {
        if (events.Count == 0) return;
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT COALESCE(MAX(version), 0) FROM events WHERE stream = $s";
            check.Parameters.AddWithValue("$s", stream);
            var actual = (long)check.ExecuteScalar()!;
            if (actual != expectedVersion) throw new ConcurrencyException(stream, expectedVersion, actual);
        }

        var now = _clock.GetUtcNow().UtcDateTime.ToString("O");
        var version = expectedVersion;
        foreach (var e in events)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO events (stream, version, data, recorded_utc) VALUES ($s, $v, $d, $t)";
            insert.Parameters.AddWithValue("$s", stream);
            insert.Parameters.AddWithValue("$v", ++version);
            insert.Parameters.AddWithValue("$d", EventJson.Serialize(e));
            insert.Parameters.AddWithValue("$t", now);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // constraint: another writer got there first
            {
                throw new ConcurrencyException(stream, expectedVersion, version);
            }
        }
        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
