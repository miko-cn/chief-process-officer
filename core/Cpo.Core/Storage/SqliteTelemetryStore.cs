using Cpo.Core.Telemetry;
using Microsoft.Data.Sqlite;

namespace Cpo.Core.Storage;

/// <summary>
/// SQLite 遥测存储实现（schema §8.2 双表分层：samples 热表 + event_log 冷表）。
/// 线程安全：内部互斥串行化写入；连接按需创建。
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
        cmd.CommandText = $"SELECT (SELECT COUNT(*) FROM {TelemetryTableRouter.SamplesTableName}) " +
                          $"+ (SELECT COUNT(*) FROM {TelemetryTableRouter.EventLogTableName})";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> CountAsync(TelemetryTable table, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {TelemetryTableRouter.TableName(table)}";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> PurgeBeforeAsync(TelemetryTable table, long tsMsBefore, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TelemetryTableRouter.TableName(table)} WHERE ts_ms < $ts";
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
        // payload = camelCase 业务字段（无 type；type 走独立列，见 schema §8.2）
        var payload = TelemetryEventSerializer.Serialize(evt);
        var table = TelemetryTableRouter.TableName(TelemetryTableRouter.TableFor(evt.Type));
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {table} (ts_ms, type, payload) VALUES ($ts, $type, $payload)";
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
        else if (!string.IsNullOrEmpty(query.TypePrefix))
        {
            clauses.Add("type LIKE $prefix || '%'");
            parameters.Add(("$prefix", query.TypePrefix));
        }

        if (query.Pid is int pid)
        {
            clauses.Add("json_extract(payload, '$.pid') = $pid");
            parameters.Add(("$pid", pid));
        }

        var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";
        // 排序必须带次级键保证确定性：同 ts_ms 的事件（一轮决策+动作常同一毫秒落库）若顺序在轮询间翻转，
        // app 增量合并会把视口内行删掉重插（表现为行闪烁消失）。单表用 id（自增=插入序）破平；
        // 跨表 UNION 用 type 破平（事件类型按表路由，同 ts 同 type 不可能跨表，故全序确定）。
        var orderSingle = query.Descending ? " ORDER BY ts_ms DESC, id DESC" : " ORDER BY ts_ms ASC, id ASC";
        var orderUnion = query.Descending ? " ORDER BY ts_ms DESC, type DESC" : " ORDER BY ts_ms ASC, type ASC";
        var limit = query.Limit is int l ? $" LIMIT {l}" : "";

        // 表选择：显式指定 > 按类型推导 > 跨表 UNION
        string sql;
        if (query.Table is TelemetryTable explicitTable)
        {
            sql = $"SELECT type, payload FROM {TelemetryTableRouter.TableName(explicitTable)}{where}{orderSingle}{limit}";
        }
        else
        {
            var inferred = InferTable(query);
            if (inferred is TelemetryTable t)
            {
                sql = $"SELECT type, payload FROM {TelemetryTableRouter.TableName(t)}{where}{orderSingle}{limit}";
            }
            else
            {
                // UNION ALL：每支子查询带相同过滤，外层统一排序
                // （UNION 结果集无 ts_ms 列，不能直接 ORDER BY ts_ms，且需 type 破平保证确定性）
                var branch = $"SELECT type, payload, ts_ms FROM {{0}}{where}";
                sql = $"SELECT type, payload FROM ({string.Format(branch, TelemetryTableRouter.SamplesTableName)} " +
                      $"UNION ALL " +
                      $"{string.Format(branch, TelemetryTableRouter.EventLogTableName)}){orderUnion}{limit}";
            }
        }

        return (sql, parameters.ToArray());
    }

    /// <summary>按查询条件推断目标表；推断不出（跨表查询）返回 null。</summary>
    private static TelemetryTable? InferTable(EventQuery query)
    {
        var type = query.Type ?? query.TypePrefix;
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        // 前缀查询："policy." 与 "sample." 明确归属；其余前缀可能跨表，走 UNION
        if (query.Type is not null)
        {
            return TelemetryTableRouter.TableFor(type);
        }

        if (type.StartsWith("policy.", StringComparison.Ordinal)
            || type.StartsWith("rule.", StringComparison.Ordinal)
            || type.StartsWith("ui.", StringComparison.Ordinal)
            || type.StartsWith("process.", StringComparison.Ordinal))
        {
            return TelemetryTable.EventLog;
        }

        if (type.StartsWith("sample.", StringComparison.Ordinal))
        {
            return TelemetryTable.Samples;
        }

        return null;
    }

    private const string SchemaSql = $$"""
        CREATE TABLE IF NOT EXISTS {{TelemetryTableRouter.SamplesTableName}} (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_ms   INTEGER NOT NULL,
            type    TEXT    NOT NULL,
            payload TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_samples_ts_type ON {{TelemetryTableRouter.SamplesTableName}} (ts_ms, type);
        CREATE INDEX IF NOT EXISTS idx_samples_type   ON {{TelemetryTableRouter.SamplesTableName}} (type);

        CREATE TABLE IF NOT EXISTS {{TelemetryTableRouter.EventLogTableName}} (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_ms   INTEGER NOT NULL,
            type    TEXT    NOT NULL,
            payload TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_event_log_ts_type ON {{TelemetryTableRouter.EventLogTableName}} (ts_ms, type);
        CREATE INDEX IF NOT EXISTS idx_event_log_type   ON {{TelemetryTableRouter.EventLogTableName}} (type);
        """;
}
