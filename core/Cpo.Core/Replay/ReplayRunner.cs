using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Cpo.Core.Telemetry;

namespace Cpo.Core.Replay;

/// <summary>回放评估结果（每周期一条）。</summary>
public sealed record ReplayFrame(
    long TsMs,
    double SystemCpuPercent,
    int ProcessCount,
    IReadOnlyList<ProcessState> Processes,
    IReadOnlyList<PolicyProposal> Proposals);

/// <summary>回放汇总统计。</summary>
public sealed record ReplaySummary(
    int FrameCount,
    int TotalProposals,
    int MatchedRuleProposals,
    double AvgProposalsPerFrame,
    long DurationMs);

/// <summary>
/// 回放框架（SPEC §8 M2 验收：能离线回放、评估策略）。
/// 读取 SQLite 遥测事件流 → 重建每个采样周期的进程/系统状态 → 逐帧送入 <see cref="PolicyEngine"/>
/// 评估 → 收集决策。与线上同一决策函数、同一参数管线（回放测试与线上走同一管线）。
/// </summary>
public static class ReplayRunner
{
    /// <summary>
    /// 回放一段遥测事件流（按 ts 升序消费），逐帧评估策略。
    /// </summary>
    /// <param name="events">已按 ts 升序排列的遥测事件流（可用 ITelemetryStore.QueryAsync 读取）。</param>
    /// <param name="rules">回放时使用的规则集（与线上同一规则模型）。</param>
    /// <param name="coreCount">系统核心数（影响系统 CPU 展示，不影响规则匹配）。</param>
    /// <param name="foregroundPid">可选：回放期间假设的前台进程（null = 无前台信息保守模式）。</param>
    /// <param name="heuristic">可选：启发式配置（null = 只跑显式规则，与线上同一参数管线）。</param>
    /// <param name="maxFrames">最多回放帧数（大轨迹防爆）。</param>
    public static ReplaySummary Evaluate(
        IEnumerable<TelemetryEvent> events,
        IReadOnlyList<PolicyRule> rules,
        int coreCount,
        int? foregroundPid = null,
        HeuristicConfig? heuristic = null,
        int maxFrames = 100_000)
    {
        var frames = BuildFrames(events, maxFrames);
        var proposalCount = 0;
        var ruleProposalCount = 0;
        var firstTs = frames.Count > 0 ? frames[0].TsMs : 0;
        var lastTs = frames.Count > 0 ? frames[^1].TsMs : 0;

        foreach (var frame in frames)
        {
            var input = new EngineInput
            {
                Processes = frame.Processes,
                SystemCpuPercent = frame.SystemCpuPercent,
                ForegroundPid = foregroundPid,
                Rules = rules,
                CoreCount = coreCount,
            };

            var proposals = PolicyEngine.Evaluate(input, heuristic);
            proposalCount += proposals.Count;
            ruleProposalCount += proposals.Count(p => p.RuleId is not null);
        }

        return new ReplaySummary(
            FrameCount: frames.Count,
            TotalProposals: proposalCount,
            MatchedRuleProposals: ruleProposalCount,
            AvgProposalsPerFrame: frames.Count > 0 ? (double)proposalCount / frames.Count : 0,
            DurationMs: lastTs - firstTs);
    }

    /// <summary>
    /// 将事件流重建为按 ts 分组的帧（每帧 = 该采样时刻的进程状态 + 系统 CPU）。
    /// 生命周期事件维护进程集合；sample.cpu 更新进程/系统 CPU。
    /// </summary>
    public static IReadOnlyList<ReplayFrame> BuildFrames(IEnumerable<TelemetryEvent> events, int maxFrames = 100_000)
    {
        // 按时间排序（事件流可能非严格有序）
        var ordered = events.OrderBy(e => e.TsMs).Take(maxFrames * 4).ToArray();

        var processes = new Dictionary<int, ProcessState>();
        double systemCpu = 0;
        var frames = new List<ReplayFrame>();

        foreach (var evt in ordered)
        {
            switch (evt)
            {
                case ProcessLifecycleEvent lc when lc.Kind == LifecycleKind.Started:
                    processes[lc.Pid] = new ProcessState(lc.Pid, lc.Name, 0, 0);
                    break;

                case ProcessLifecycleEvent lc when lc.Kind == LifecycleKind.Exited:
                    processes.Remove(lc.Pid);
                    break;

                case CpuSampleEvent cpu when cpu.Scope == SampleScope.System:
                    systemCpu = cpu.CpuPercent;
                    break;

                case CpuSampleEvent cpu when cpu.Scope == SampleScope.Process:
                    if (processes.TryGetValue(cpu.Pid!.Value, out var existing))
                    {
                        processes[cpu.Pid.Value] = existing with { CpuPercent = cpu.CpuPercent };
                    }

                    break;
            }

            // 每个采样点产生一帧（以系统 CPU 采样或进程 CPU 采样为周期锚点）
            if (evt is CpuSampleEvent { Scope: SampleScope.System })
            {
                frames.Add(new ReplayFrame(
                    TsMs: evt.TsMs,
                    SystemCpuPercent: systemCpu,
                    ProcessCount: processes.Count,
                    Processes: processes.Values.ToArray(),
                    Proposals: Array.Empty<PolicyProposal>()));
            }
        }

        return frames;
    }
}
