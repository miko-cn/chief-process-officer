namespace Cpo.Core.Engine;

/// <summary>一次生效中的干预（记录原值，供恢复）。</summary>
public sealed record ActiveIntervention(
    int Pid,
    string Name,
    long StartedMs,
    long? DurationMs,
    ProposalActionKind Action,
    int? TargetPriorityClass,
    ulong? TargetAffinityMask,
    ProcessControlState OriginalState);

/// <summary>执行事件（供上层写入 policy.action 决策日志）。</summary>
public sealed record ExecutionEvent(
    PolicyProposal Proposal,
    bool Succeeded,
    string? Error,
    ProcessControlState? OriginalState,
    long ExecutedMs);

/// <summary>
/// 执行路径（ExecutionPath）——确定性屏障的执行侧。
/// 只接受已采纳的建议（上层传入），执行实际干预；记录原值，条件解除/超时/停止时自动恢复。
/// 核心不碰 Win32：所有进程操作经 <see cref="IProcessController"/>。
/// </summary>
public sealed class ExecutionPath
{
    private readonly IProcessController _controller;
    private readonly object _gate = new();
    private readonly Dictionary<int, ActiveIntervention> _active = new();
    private readonly Dictionary<int, long> _lastRestoredMs = new();
    private readonly List<ExecutionEvent> _executionLog = new();

    public ExecutionPath(IProcessController controller)
    {
        _controller = controller;
    }

    /// <summary>当前生效中的干预（按 PID）。</summary>
    public IReadOnlyDictionary<int, ActiveIntervention> ActiveInterventions
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<int, ActiveIntervention>(_active);
            }
        }
    }

    /// <summary>最近执行事件（决策日志来源，按时间序）。</summary>
    public IReadOnlyList<ExecutionEvent> ExecutionLog
    {
        get
        {
            lock (_gate)
            {
                return _executionLog.ToArray();
            }
        }
    }

    /// <summary>
    /// 执行一条建议。若该进程已有生效干预：同动作同参数 → 跳过（幂等）；
    /// 不同参数 → 先恢复原值再应用新干预。
    /// </summary>
    public ExecutionEvent Execute(PolicyProposal proposal)
    {
        lock (_gate)
        {
            // 幂等：已生效且参数相同则跳过
            if (_active.TryGetValue(proposal.TargetPid, out var existing)
                && SameParameters(existing, proposal))
            {
                return new ExecutionEvent(proposal, true, "already active", existing.OriginalState, proposal.TsMs);
            }

            // 冷却期：恢复后 DurationMs 内不重复应用同一建议（防抖动）
            if (proposal.DurationMs is long cooldownMs
                && _lastRestoredMs.TryGetValue(proposal.TargetPid, out var lastRestored)
                && proposal.TsMs - lastRestored < cooldownMs)
            {
                return new ExecutionEvent(proposal, true, "cooldown", null, proposal.TsMs);
            }

            // 已有不同干预 → 先恢复
            if (existing is not null)
            {
                RestoreLocked(existing, proposal.TsMs);
            }

            var original = _controller.GetState(proposal.TargetPid);
            if (original is null)
            {
                var evt = new ExecutionEvent(proposal, false, "process not found", null, proposal.TsMs);
                _executionLog.Add(evt);
                return evt;
            }

            var result = Apply(proposal);
            if (result.Succeeded)
            {
                _active[proposal.TargetPid] = new ActiveIntervention(
                    Pid: proposal.TargetPid,
                    Name: proposal.TargetName,
                    StartedMs: proposal.TsMs,
                    DurationMs: proposal.DurationMs,
                    Action: proposal.Action,
                    TargetPriorityClass: proposal.PriorityClass,
                    TargetAffinityMask: proposal.AffinityMask,
                    OriginalState: original);
            }

            var execEvent = new ExecutionEvent(proposal, result.Succeeded, result.Error, original, proposal.TsMs);
            _executionLog.Add(execEvent);
            return execEvent;
        }
    }

    /// <summary>
    /// 恢复指定进程到原值。进程不存在/无干预 → 无操作。
    /// 恢复动作同样记入执行日志（SPEC：恢复动作同样写入决策日志）。
    /// </summary>
    public ExecutionEvent? Restore(int pid)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(pid, out var intervention))
            {
                return null;
            }

            return RestoreLocked(intervention, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>
    /// 清理所有超时干预（返回本次恢复的数量）。由上层周期调用。
    /// 时长 null 的干预不过期；进程已消失的干预移除但不产生恢复事件。
    /// </summary>
    public int ReapExpired(long nowMs)
    {
        lock (_gate)
        {
            var expired = _active.Values
                .Where(i => i.DurationMs is long d && nowMs - i.StartedMs >= d)
                .ToArray();

            var removed = 0;
            foreach (var intervention in expired)
            {
                // 进程已消失（GetState 为 null）→ 干预自然失效，静默移除
                if (_controller.GetState(intervention.Pid) is null)
                {
                    _active.Remove(intervention.Pid);
                    continue;
                }

                RestoreLocked(intervention, nowMs);
                removed++;
            }

            return removed;
        }
    }

    /// <summary>恢复全部干预（引擎停止时调用，SPEC：引擎退出时自动恢复原值）。</summary>
    public int RestoreAll()
    {
        lock (_gate)
        {
            var pids = _active.Keys.ToArray();
            var restored = 0;
            foreach (var pid in pids)
            {
                if (_active.TryGetValue(pid, out var intervention))
                {
                    RestoreLocked(intervention, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    restored++;
                }
            }

            return restored;
        }
    }

    private ExecutionEvent RestoreLocked(ActiveIntervention intervention, long restoreMs)
    {
        var proposal = new PolicyProposal
        {
            TsMs = restoreMs,
            Trigger = "restore",
            TargetPid = intervention.Pid,
            TargetName = intervention.Name,
            Action = ProposalActionKind.Restore,
            PriorityClass = intervention.OriginalState.PriorityClass,
            AffinityMask = intervention.OriginalState.AffinityMask,
            DurationMs = null,
            Reason = $"恢复 {intervention.Name} (pid {intervention.Pid}) 到原值（优先级 0x{intervention.OriginalState.PriorityClass:X}，亲和 0x{intervention.OriginalState.AffinityMask:X}）",
            RuleId = null,
        };

        // 原值恢复：优先级和亲和性都还原（原始状态包含两者）
        var results = new List<InterventionResult>();
        if (intervention.Action is ProposalActionKind.SetPriority or ProposalActionKind.SetBoth)
        {
            results.Add(_controller.SetPriorityClass(intervention.Pid, intervention.OriginalState.PriorityClass));
        }

        if (intervention.Action is ProposalActionKind.SetAffinity or ProposalActionKind.SetBoth)
        {
            results.Add(_controller.SetAffinityMask(intervention.Pid, intervention.OriginalState.AffinityMask));
        }

        var ok = results.All(r => r.Succeeded);
        var evt = new ExecutionEvent(proposal, ok, ok ? null : "restore failed", intervention.OriginalState, proposal.TsMs);
        _executionLog.Add(evt);
        _active.Remove(intervention.Pid);
        _lastRestoredMs[intervention.Pid] = proposal.TsMs;
        return evt;
    }

    private InterventionResult Apply(PolicyProposal proposal)
    {
        return proposal.Action switch
        {
            ProposalActionKind.SetPriority => _controller.SetPriorityClass(proposal.TargetPid, proposal.PriorityClass!.Value),
            ProposalActionKind.SetAffinity => _controller.SetAffinityMask(proposal.TargetPid, proposal.AffinityMask!.Value),
            ProposalActionKind.SetBoth => ApplyBoth(proposal),
            ProposalActionKind.Restore => throw new InvalidOperationException("Restore 建议不应通过 Execute 应用"),
            _ => throw new ArgumentOutOfRangeException(nameof(proposal.Action)),
        };
    }

    private InterventionResult ApplyBoth(PolicyProposal proposal)
    {
        var priority = _controller.SetPriorityClass(proposal.TargetPid, proposal.PriorityClass!.Value);
        if (!priority.Succeeded)
        {
            return priority;
        }

        var affinity = _controller.SetAffinityMask(proposal.TargetPid, proposal.AffinityMask!.Value);
        return affinity;
    }

    private static bool SameParameters(ActiveIntervention existing, PolicyProposal proposal) =>
        existing.Action == proposal.Action
        && existing.TargetPriorityClass == proposal.PriorityClass
        && existing.TargetAffinityMask == proposal.AffinityMask;
}
