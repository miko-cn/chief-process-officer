using Cpo.Core.Rules;

namespace Cpo.Core.Engine;

/// <summary>单个进程的引擎输入状态。</summary>
public sealed record ProcessState(
    int Pid,
    string Name,
    double CpuPercent,
    long WorkingSetBytes);

/// <summary>
/// 引擎输入（SPEC §6 数据流输入）：进程遥测 + 系统状态 + 前台状态 + 用户显式规则。
/// 回放与线上共用同一结构（同一参数管线）。
/// </summary>
public sealed record EngineInput
{
    /// <summary>当前进程状态快照。</summary>
    public required IReadOnlyList<ProcessState> Processes { get; init; }

    /// <summary>系统整体 CPU 占用率（0~100）。</summary>
    public required double SystemCpuPercent { get; init; }

    /// <summary>前台进程 PID（无前台信息时为 null —— 启发式降级保守模式的输入位）。</summary>
    public int? ForegroundPid { get; init; }

    /// <summary>用户显式规则（最高优先级输入）。</summary>
    public required IReadOnlyList<PolicyRule> Rules { get; init; }

    /// <summary>逻辑核心数。</summary>
    public required int CoreCount { get; init; }
}
