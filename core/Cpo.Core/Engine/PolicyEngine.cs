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
    /// <summary>
    /// 评估一次决策。
    /// 规则按列表顺序优先（第一条匹配生效）；不匹配规则的进程不产生建议。
    /// 前台进程保护：已知前台进程不降优（保守，避免误伤用户正在使用的程序）。
    /// </summary>
    public static IReadOnlyList<PolicyProposal> Evaluate(EngineInput input)
    {
        var proposals = new List<PolicyProposal>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var process in input.Processes)
        {
            if (input.ForegroundPid == process.Pid)
            {
                continue; // 前台保护
            }

            var rule = FirstMatchingRule(input.Rules, process.Name);
            if (rule is null)
            {
                continue;
            }

            proposals.Add(BuildProposal(now, process, rule));
        }

        return proposals;
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
