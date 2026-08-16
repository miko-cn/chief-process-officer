using System.Text.Json;
using Cpo.Core.Telemetry;
using Microsoft.Data.Sqlite;

namespace Cpo.Core.Storage;

/// <summary>
/// SQLite 遥测存储实现（schema §8 落盘形态：单表 events + ts/type 索引）。
/// 线程安全：内部互斥串行化写入；连接按需创建，WAL 模式。
/// </summary>
public sealed class SqliteTelemetryStore : ITelemetryStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    /// <summary>连接串（诊断/测试用）。</summary>
    public string ConnectionStringForDebug => _connectionString;

    /// <param name="databasePath">SQLite 文件路径；或 ":memory:" / "file:xxx?mode=memory&amp;cache=shared" 内存库（测试用）。</param>
    public SqliteTelemetryStore(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = databasePath == ":memory:" ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connectionString = builder.ToString();
    }

    /// <summary>创建共享内存库（同一 store 的所有连接共享同一库，多连接安全）。</summary>
    public static SqliteTelemetryStore CreateInMemory() =>
        new($"file:cpo-test-{Guid.NewGuid():N}?mode=memory&cache=shared");

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var conn = await OpenAsync(ct);
            await ExecuteAsync(conn, SchemaSql, ct);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendAsync(TelemetryEvent evt, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await InsertAsync(conn, evt, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendBatchAsync(IEnumerable<TelemetryEvent> events, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var evt in events)
            {
                ct.ThrowIfCancellationRequested();
                await InsertAsync(conn, evt, ct, tx);
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async IAsyncEnumerable<TelemetryEvent> QueryAsync(
        EventQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (sql, parameters) = BuildSelect(query);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var type = reader.GetString(reader.GetOrdinal("type"));
            var payload = reader.GetString(reader.GetOrdinal("payload"));
            yield return TelemetryEventSerializer.Deserialize(type, payload);
        }
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM events";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> PurgeBeforeAsync(long tsMsBefore, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM events WHERE ts_ms < $ts";
            cmd.Parameters.AddWithValue("$ts", tsMsBefore);
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            SqliteConnection.ClearAllPools();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertAsync(
        SqliteConnection conn, TelemetryEvent evt, CancellationToken ct, SqliteTransaction? tx = null)
    {
        // payload = camelCase 业务字段（无 type；type 走独立列，见 schema §8）
        var payload = TelemetryEventSerializer.Serialize(evt);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO events (ts_ms, type, payload) VALUES ($ts, $type, $payload)";
        cmd.Parameters.AddWithValue("$ts", evt.TsMs);
        cmd.Parameters.AddWithValue("$type", evt.Type);
        cmd.Parameters.AddWithValue("$payload", payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static (string Sql, (string Name, object Value)[] Parameters) BuildSelect(EventQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object)>();

        if (query.FromMs is long from)
        {
            clauses.Add("ts_ms >= $from");
            parameters.Add(("$from", from));
        }

        if (query.ToMs is long to)
        {
            clauses.Add("ts_ms <= $to");
            parameters.Add(("$to", to));
        }

        if (!string.IsNullOrEmpty(query.Type))
        {
            clauses.Add("type = $type");
            parameters.Add(("$type", query.Type));
        }

        if (query.Pid is int pid)
        {
            clauses.Add("json_extract(payload, '$.pid') = $pid");
            parameters.Add(("$pid", pid));
        }

        var sql = "SELECT type, payload FROM events";
        if (clauses.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", clauses);
        }

        sql += " ORDER BY ts_ms ASC";
        if (query.Limit is int limit)
        {
            sql += " LIMIT " + limit;
        }

        return (sql, parameters.ToArray());
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS events (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_ms   INTEGER NOT NULL,
            type    TEXT    NOT NULL,
            payload TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_events_ts_type ON events (ts_ms, type);
        CREATE INDEX IF NOT EXISTS idx_events_type   ON events (type);
        """;
}
