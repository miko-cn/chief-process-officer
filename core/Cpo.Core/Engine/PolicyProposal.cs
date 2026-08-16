namespace Cpo.Core.Engine;

/// <summary>
/// 建议动作类型（与 schema §6 policy.action.kind 一致）。
/// </summary>
public enum ProposalActionKind
{
    SetPriority,
    SetAffinity,
    SetBoth,
    Restore,
}

/// <summary>
/// 引擎输出的建议（ProposalBus 承载，ExecutionPath 执行）。
/// 含理由与持续时长（SPEC：输出 = 动作 + 理由 + 持续时间）。
/// </summary>
public sealed record PolicyProposal
{
    /// <summary>决策时间（Unix 毫秒 UTC）。</summary>
    public required long TsMs { get; init; }

    /// <summary>触发条件描述（如 "rule:msbuild" / "cpu.storm"）。</summary>
    public required string Trigger { get; init; }

    /// <summary>目标进程。</summary>
    public required int TargetPid { get; init; }

    /// <summary>目标进程名。</summary>
    public required string TargetName { get; init; }

    /// <summary>建议动作。</summary>
    public required ProposalActionKind Action { get; init; }

    /// <summary>目标优先级类（Windows 常量）。</summary>
    public int? PriorityClass { get; init; }

    /// <summary>目标亲和性掩码。</summary>
    public ulong? AffinityMask { get; init; }

    /// <summary>建议生效时长（毫秒）；null = 持续生效。</summary>
    public long? DurationMs { get; init; }

    /// <summary>人类可读理由（决策日志双视图之一）。</summary>
    public required string Reason { get; init; }

    /// <summary>来源规则 ID（显式规则触发时非空）。</summary>
    public string? RuleId { get; init; }
}
