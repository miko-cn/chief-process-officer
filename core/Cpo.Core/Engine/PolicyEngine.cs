using Cpo.Core.Rules;

namespace Cpo.Core.Engine;

/// <summary>
/// 策略引擎（M2：显式规则优先）。
/// 纯逻辑决策函数：输入进程遥测 + 规则 → 输出建议（动作 + 理由 + 持续时间）。
/// 无 I/O、无 OS 依赖，可单测；回放与线上共用同一决策函数（SPEC §6 数据流）。
///
/// 确定性屏障：本引擎只产出 <see cref="PolicyProposal"/>（走 ProposalBus 建议通道），
/// 不直接执行任何动作；执行由 <see cref="ExecutionPath"/> 独立完成。
/// </summary>
public static class PolicyEngine
{
    /// <summary>系统关键进程名（进程名不含 .exe 后缀）。启发式绝不干预，防止把系统拖垮。</summary>
    private static readonly HashSet<string> SystemCriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system idle process", "system", "registry", "memory compression", "secure system",
        "svchost", "csrss", "dwm", "wininit", "winlogon", "services", "lsass", "smss", "explorer",
        // 引擎自身：饱和时采样/评估进程自身 CPU 也高，绝不能把自己降了
        "cpo.service", "cpo.app",
    };

    /// <summary>
    /// 评估一次决策。
    /// 规则按列表顺序优先（第一条匹配生效，启发式不覆盖规则）。
    /// 启发式（<paramref name="heuristic"/> 非 null 时启用）：响应性保护——
    /// 系统 CPU 饱和 + 进程挤占 + 非关键 三条件齐备才干预（会话⑲定案）。
    /// 前台进程保护：已知前台进程不降优（保守，避免误伤用户正在使用的程序）。
    /// </summary>
    public static IReadOnlyList<PolicyProposal> Evaluate(EngineInput input, HeuristicConfig? heuristic = null)
    {
        var proposals = new List<PolicyProposal>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 无前台信息 → 启发式降级保守模式（SPEC §6 定案：不主动降后台进程，避免误伤）。
        // 显式规则不受影响（规则是用户明确意图）。
        var heuristicArmed = heuristic is not null
                             && input.ForegroundPid is not null
                             && input.SystemCpuPercent >= heuristic.SystemSaturationPercent;

        foreach (var process in input.Processes)
        {
            if (input.ForegroundPid == process.Pid)
            {
                continue; // 前台保护
            }

            var rule = FirstMatchingRule(input.Rules, process.Name);
            if (rule is not null)
            {
                proposals.Add(BuildProposal(now, process, rule));
                continue; // 规则优先：命中规则的进程不再走启发式
            }

            if (!heuristicArmed)
            {
                continue;
            }

            // 前台进程树子进程（IDE 的 rg/编译、AI agent 工具等）：按标准档降级——
            // 降子进程只影响它自己（调度器仍优先给 Normal 的前台进程），代价仅是变慢，
            // 不影响前台响应度（会话⑳c 定案，修正"树内绝不降"的过度保护）。
            // 近期前台程序（用户高频使用）：温和降级（更高阈值 + 更短时长）
            var recentForeground = input.RecentForegroundPids?.Contains(process.Pid) == true;
            if (IsHeuristicTarget(process, heuristic!, recentForeground))
            {
                proposals.Add(BuildHeuristicProposal(now, process, input.SystemCpuPercent, heuristic!, recentForeground));
            }
        }

        return proposals;
    }

    /// <summary>启发式目标判定（三条件中的后两条：挤占者 + 非关键；系统饱和已由调用方保证）。
    /// 近期前台程序用更高的挤占阈值（更谨慎才动它）。</summary>
    private static bool IsHeuristicTarget(ProcessState process, HeuristicConfig heuristic, bool recentForeground)
    {
        var threshold = recentForeground ? heuristic.RecentForegroundCpuPercent : heuristic.ProcessCpuPercent;
        return process.CpuPercent >= threshold
               && !SystemCriticalNames.Contains(process.Name);
    }

    private static PolicyProposal BuildHeuristicProposal(
        long now, ProcessState process, double systemCpuPercent, HeuristicConfig heuristic, bool recentForeground)
    {
        var durationMs = recentForeground ? heuristic.RecentForegroundDurationMs : heuristic.DurationMs;
        var reason = recentForeground
            ? $"启发式: 系统 CPU 饱和（{systemCpuPercent:0}%），近期前台程序 {process.Name} 严重挤占 {process.CpuPercent:0}%，" +
              $"谨慎降级 → {DescribePriority(heuristic.PriorityClass)}（{durationMs / 1000} 秒后自动恢复）"
            : $"启发式: 系统 CPU 饱和（{systemCpuPercent:0}%），进程 {process.Name} 挤占 {process.CpuPercent:0}%，" +
              $"建议优先级 → {DescribePriority(heuristic.PriorityClass)}（{durationMs / 1000} 秒后自动恢复）";

        return new PolicyProposal
        {
            TsMs = now,
            Trigger = "heuristic.saturation",
            TargetPid = process.Pid,
            TargetName = process.Name,
            Action = ProposalActionKind.SetPriority,
            PriorityClass = heuristic.PriorityClass,
            DurationMs = durationMs,
            Reason = reason,
            RuleId = null,
        };
    }

    private static PolicyRule? FirstMatchingRule(IReadOnlyList<PolicyRule> rules, string processName)
    {
        foreach (var rule in rules)
        {
            if (RuleMatcher.Matches(rule, processName))
            {
                return rule;
            }
        }

        return null;
    }

    private static PolicyProposal BuildProposal(long now, ProcessState process, PolicyRule rule)
    {
        var action = rule.Action switch
        {
            RuleActionKind.SetPriority => ProposalActionKind.SetPriority,
            RuleActionKind.SetAffinity => ProposalActionKind.SetAffinity,
            RuleActionKind.SetBoth => ProposalActionKind.SetBoth,
            _ => throw new ArgumentOutOfRangeException(nameof(rule.Action)),
        };

        var reason = rule.Action switch
        {
            RuleActionKind.SetPriority => $"规则 {rule.Id}: 进程 {process.Name} 匹配 '{rule.ProcessPattern}'，建议优先级 → {DescribePriority(rule.PriorityClass)}",
            RuleActionKind.SetAffinity => $"规则 {rule.Id}: 进程 {process.Name} 匹配 '{rule.ProcessPattern}'，建议亲和性 → {DescribeAffinity(rule.AffinityMask)}",
            _ => $"规则 {rule.Id}: 进程 {process.Name} 匹配 '{rule.ProcessPattern}'，建议优先级 + 亲和性调整",
        };

        return new PolicyProposal
        {
            TsMs = now,
            Trigger = $"rule:{rule.Id}",
            TargetPid = process.Pid,
            TargetName = process.Name,
            Action = action,
            PriorityClass = rule.PriorityClass,
            AffinityMask = rule.AffinityMask,
            DurationMs = rule.DurationMs,
            Reason = reason,
            RuleId = rule.Id,
        };
    }

    private static string DescribePriority(int? priorityClass) => priorityClass switch
    {
        0x40 => "Idle",
        0x4000 => "BelowNormal",
        0x20 => "Normal",
        0x8000 => "AboveNormal",
        0x80 => "High",
        0x100 => "Realtime",
        _ => priorityClass?.ToString() ?? "未指定",
    };

    private static string DescribeAffinity(ulong? mask)
    {
        if (mask is not ulong m)
        {
            return "未指定";
        }

        var cores = System.Numerics.BitOperations.PopCount(m);
        return $"0x{m:X}（{cores} 核）";
    }
}
