using System.Text.Json;
using Cpo.Core.Telemetry;

namespace Cpo.Core.Engine;

/// <summary>
/// 决策日志桥接（SPEC §7：每次干预留痕，机器可读）。
/// 把 ProposalBus 建议 / ExecutionPath 执行事件 / RuleStore 变更
/// 转为 schema 事件（policy.decision / policy.action / rule.changed）供落盘。
/// </summary>
public static class DecisionLogger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>建议 → policy.decision 事件（输入快照 + 结论双 JSON）。</summary>
    public static PolicyDecisionEvent ToDecisionEvent(PolicyProposal proposal, DecisionMode mode)
    {
        var inputSnapshot = new
        {
            trigger = proposal.Trigger,
            targetPid = proposal.TargetPid,
            targetName = proposal.TargetName,
            cpuPercent = 0.0, // M2 引擎输入不含 CPU 细节快照，M3 启发式接入后补充
        };

        var conclusion = new
        {
            accepted = true,
            action = proposal.Action.ToString(),
            priorityClass = proposal.PriorityClass,
            affinityMask = proposal.AffinityMask,
            durationMs = proposal.DurationMs,
            reason = proposal.Reason,
            ruleId = proposal.RuleId,
        };

        return new PolicyDecisionEvent(
            TsMs: proposal.TsMs,
            Trigger: proposal.Trigger,
            TargetPid: proposal.TargetPid,
            TargetName: proposal.TargetName,
            ProposedActionsJson: JsonSerializer.Serialize(new[] { proposal.Action.ToString() }, JsonOptions),
            InputSnapshotJson: JsonSerializer.Serialize(inputSnapshot, JsonOptions),
            Mode: mode,
            ConclusionJson: JsonSerializer.Serialize(conclusion, JsonOptions));
    }

    /// <summary>执行事件 → policy.action 事件（含原值 previous 与结果）。</summary>
    public static PolicyActionEvent ToActionEvent(ExecutionEvent execution)
    {
        var kind = execution.Proposal.Action switch
        {
            ProposalActionKind.SetPriority => ActionKind.SetPriority,
            ProposalActionKind.SetAffinity => ActionKind.SetAffinity,
            ProposalActionKind.SetBoth => ActionKind.SetPriority, // 双动作以优先级为主记录，参数含亲和
            ProposalActionKind.Restore => ActionKind.Restore,
            _ => ActionKind.SetPriority,
        };

        var parameters = new
        {
            priorityClass = execution.Proposal.PriorityClass,
            affinityMask = execution.Proposal.AffinityMask,
        };

        var previous = execution.OriginalState is { } orig
            ? new { priorityClass = orig.PriorityClass, affinityMask = orig.AffinityMask }
            : null;

        return new PolicyActionEvent(
            TsMs: execution.ExecutedMs,
            Kind: kind,
            TargetPid: execution.Proposal.TargetPid,
            TargetName: execution.Proposal.TargetName,
            ParametersJson: JsonSerializer.Serialize(parameters, JsonOptions),
            PreviousJson: previous is null ? null : JsonSerializer.Serialize(previous, JsonOptions),
            Result: execution.Succeeded ? ActionResult.Succeeded : ActionResult.Failed,
            Error: execution.Error,
            DurationMs: execution.Proposal.DurationMs);
    }
}
