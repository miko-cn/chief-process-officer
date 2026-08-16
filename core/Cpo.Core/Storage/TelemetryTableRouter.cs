using Cpo.Core.Telemetry;

namespace Cpo.Core.Storage;

/// <summary>存储表（schema §8.1 双表分层）。</summary>
public enum TelemetryTable
{
    /// <summary>热表：高频采样，短保留（决策输入 + 近期诊断）。</summary>
    Samples,

    /// <summary>冷表：低频日志，长保留（审阅/诊断/AI 语料）。</summary>
    EventLog,
}

/// <summary>
/// 事件 → 表的单一事实来源（schema §8.1 路由规则）。
/// 高频采样进 samples（短命）；其余进 event_log（长命）。
/// </summary>
public static class TelemetryTableRouter
{
    public const string SamplesTableName = "samples";
    public const string EventLogTableName = "event_log";

    /// <summary>事件类型 → 存储表。</summary>
    public static TelemetryTable TableFor(string eventType) => eventType switch
    {
        TelemetryEventTypes.CpuSample or TelemetryEventTypes.MemorySample => TelemetryTable.Samples,
        _ => TelemetryTable.EventLog,
    };

    /// <summary>存储表 → 表名。</summary>
    public static string TableName(TelemetryTable table) => table switch
    {
        TelemetryTable.Samples => SamplesTableName,
        TelemetryTable.EventLog => EventLogTableName,
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };
}
