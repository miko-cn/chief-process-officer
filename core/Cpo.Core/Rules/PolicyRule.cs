using Cpo.Core.Telemetry;

namespace Cpo.Core.Rules;

/// <summary>
/// 降优动作类型（SPEC §6：优先级 + CPU 亲和性硬降为主；Job Object 限流为可选辅助）。
/// </summary>
public enum RuleActionKind
{
    /// <summary>设置优先级类（SetPriorityClass）。</summary>
    SetPriority,

    /// <summary>设置 CPU 亲和性掩码（SetProcessAffinityMask）。</summary>
    SetAffinity,

    /// <summary>优先级 + 亲和性同时设置。</summary>
    SetBoth,
}

/// <summary>
/// 显式规则（SPEC §6：显式规则 = 最高优先级输入；启发式 = 默认决策函数，M2 只做规则）。
/// 进程名匹配支持通配符（* 任意串，? 单字符），大小写不敏感。
/// </summary>
public sealed record PolicyRule
{
    /// <summary>规则 ID（rule.changed 事件关联用）。</summary>
    public required string Id { get; init; }

    /// <summary>进程名模式（如 "msbuild.exe"、"chrome*.exe"、"*build*"）。</summary>
    public required string ProcessPattern { get; init; }

    /// <summary>动作类型。</summary>
    public required RuleActionKind Action { get; init; }

    /// <summary>目标优先级类（Windows 优先级类常量值，见 ProcessPriorityClass）。SetPriority/SetBoth 时必填。</summary>
    public int? PriorityClass { get; init; }

    /// <summary>目标亲和性掩码（位 N = 逻辑核 N）。SetAffinity/SetBoth 时必填。</summary>
    public ulong? AffinityMask { get; init; }

    /// <summary>建议生效时长（毫秒）。超过后执行路径自动恢复原值；null = 持续生效直到条件解除。</summary>
    public long? DurationMs { get; init; }

    /// <summary>是否启用。禁用规则不参与匹配（rule.changed 的 disabled 状态）。</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>规则来源（user=用户显式创建；suggestion=引擎建议待采纳）。</summary>
    public RuleChangeSource Source { get; init; } = RuleChangeSource.User;
}
