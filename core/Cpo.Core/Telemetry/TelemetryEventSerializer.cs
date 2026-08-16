using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cpo.Core.Telemetry;

/// <summary>
/// 遥测事件 JSON 序列化契约（schema §0/§8）：
/// - payload = 业务字段 JSON，字段名 camelCase，**不含 type**（type 是 SQLite 独立列）
/// - 落盘/回放由存储层按 type 列 dispatch，不依赖多态判别符
///   （System.Text.Json 多态序列化在 net8 下对抽象基类属性 + 判别符存在冲突，见 M1 记录）
/// </summary>
public static class TelemetryEventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>序列化事件为 payload JSON（camelCase，无 type 字段）。</summary>
    public static string Serialize(TelemetryEvent evt) =>
        JsonSerializer.Serialize(evt, evt.GetType(), Options);

    /// <summary>按事件类型反序列化 payload JSON。</summary>
    public static TelemetryEvent Deserialize(string type, string payload) => type switch
    {
        TelemetryEventTypes.ProcessLifecycle => JsonSerializer.Deserialize<ProcessLifecycleEvent>(payload, Options)!,
        TelemetryEventTypes.CpuSample => JsonSerializer.Deserialize<CpuSampleEvent>(payload, Options)!,
        TelemetryEventTypes.MemorySample => JsonSerializer.Deserialize<MemorySampleEvent>(payload, Options)!,
        TelemetryEventTypes.Foreground => JsonSerializer.Deserialize<ForegroundEvent>(payload, Options)!,
        TelemetryEventTypes.PolicyDecision => JsonSerializer.Deserialize<PolicyDecisionEvent>(payload, Options)!,
        TelemetryEventTypes.PolicyAction => JsonSerializer.Deserialize<PolicyActionEvent>(payload, Options)!,
        TelemetryEventTypes.RuleChanged => JsonSerializer.Deserialize<RuleChangedEvent>(payload, Options)!,
        TelemetryEventTypes.InterventionToggled => JsonSerializer.Deserialize<InterventionToggledEvent>(payload, Options)!,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "未知事件类型，与 docs/schema.md 契约不符"),
    };
}
