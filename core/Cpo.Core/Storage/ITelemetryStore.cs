using Cpo.Core.Telemetry;

namespace Cpo.Core.Storage;

/// <summary>遥测事件查询条件（schema §8 回放约定：按时间序 + type 过滤）。</summary>
public sealed record EventQuery
{
    /// <summary>起始时间（Unix 毫秒，含）。null = 不限。</summary>
    public long? FromMs { get; init; }

    /// <summary>结束时间（Unix 毫秒，含）。null = 不限。</summary>
    public long? ToMs { get; init; }

    /// <summary>事件类型过滤（schema 枚举值）。null = 全部。</summary>
    public string? Type { get; init; }

    /// <summary>按进程 PID 过滤（作用于 payload 中的 pid 字段）。null = 全部。</summary>
    public int? Pid { get; init; }

    /// <summary>返回上限。null = 不限。</summary>
    public int? Limit { get; init; }
}

/// <summary>
/// 遥测事件存储接口。落盘形态 = SQLite 单表 events（schema §8）。
/// core 层只依赖该接口，具体实现（SQLite/内存/回放）可替换，保证可测试性。
/// </summary>
public interface ITelemetryStore
{
    /// <summary>初始化存储（建表、迁移）。幂等。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>追加单条事件。</summary>
    Task AppendAsync(TelemetryEvent evt, CancellationToken ct = default);

    /// <summary>批量追加事件（内部事务提交）。</summary>
    Task AppendBatchAsync(IEnumerable<TelemetryEvent> events, CancellationToken ct = default);

    /// <summary>按查询条件回放事件（按 ts_ms 升序）。</summary>
    IAsyncEnumerable<TelemetryEvent> QueryAsync(EventQuery query, CancellationToken ct = default);

    /// <summary>事件总数。</summary>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>删除早于指定时间戳的全部事件（保留策略）。返回删除条数。</summary>
    Task<long> PurgeBeforeAsync(long tsMsBefore, CancellationToken ct = default);
}
