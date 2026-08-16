using System.Text.Json.Serialization;

namespace Cpo.Core.Telemetry;

/// <summary>
/// 遥测事件类型枚举（对应 docs/schema.md §0 事件类型表）。
/// 值即落盘到 SQLite `events.type` 列 / JSON 流的字符串。
/// </summary>
public static class TelemetryEventTypes
{
    public const string ProcessLifecycle = "process.lifecycle";
    public const string CpuSample = "sample.cpu";
    public const string MemorySample = "sample.memory";
    public const string Foreground = "ui.foreground";
    public const string PolicyDecision = "policy.decision";
    public const string PolicyAction = "policy.action";
    public const string RuleChanged = "rule.changed";
    public const string InterventionToggled = "policy.intervention_toggled";
}

/// <summary>
/// 遥测事件基类。所有事件共享 <see cref="TsMs"/>（Unix 毫秒 UTC，见 schema §0）。
/// JSON 序列化契约见 <see cref="TelemetryEventSerializer"/>：
/// payload 为 camelCase 业务字段（不含 type），type 由 <see cref="Type"/> 提供并作为独立列落盘。
/// </summary>
public abstract record TelemetryEvent
{
    protected TelemetryEvent(long tsMs) => TsMs = tsMs;

    /// <summary>事件时间戳：Unix 毫秒（UTC）。schema §0 约定。</summary>
    public long TsMs { get; init; }

    /// <summary>事件类型（schema 枚举字符串）。</summary>
    [JsonIgnore]
    public abstract string Type { get; }
}
